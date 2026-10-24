using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed class ResourceInspectionService(Application application, WeakObjectRegistry objects) : IDisposable
{
    private readonly Dictionary<(long Id, string Key), ResourceEdit> _edits = [];
    private readonly Dictionary<long, Location> _locations = [];
    private const int MaxText = 1024;
    private const int MaxKey = 256;

    public ResourceTreeSnapshot GetTree(ResourceTreeQueryRequest request)
    {
        ValidateBudget(request.MaxNodes, 1024, nameof(request.MaxNodes));
        var budget = new Budget(request.MaxNodes, 10000);
        var roots = new List<ResourceProviderInfo>();
        var visited = new HashSet<IResourceProvider>(ReferenceEqualityComparer.Instance);
        var ancestors = new HashSet<IResourceProvider>(ReferenceEqualityComparer.Instance);
        _locations.Clear();
        AddProvider(application.Resources, "Application.Resources", new("Application.Resources", "Application", null), roots);
        var stylesVisited = new HashSet<IStyle>(ReferenceEqualityComparer.Instance);
        AddStyles(application.Styles, "Application.Styles");
        if (request.ObjectId is { } id)
        {
            if (objects.Get(id) is not StyledElement anchor)
                throw new RpcException("unsupported_object", "The selected object has no resource scope.");
            var elementVisited = new HashSet<StyledElement>(ReferenceEqualityComparer.Instance);
            for (var current = anchor; current is not null && !budget.Truncated; current = current.Parent)
            {
                if (!elementVisited.Add(current)) { budget.Note("A cycle in the element resource scope was skipped."); break; }
                var name = Clip(current.GetType().Name + (current.Name is null ? "" : " #" + current.Name), MaxKey);
                var path = $"Element#{objects.GetId(current)}";
                if (((IResourceNode)current).HasResources)
                    AddProvider(current.Resources, name, new(path + ".Resources", "Element", null), roots);
                AddStyles(current.Styles, path + ".Styles");
                if (elementVisited.Count >= 128) { budget.Truncated = true; break; }
            }
        }
        return new(roots, budget.Nodes, budget.Truncated, budget.Notes.ToArray());

        void AddProvider(IResourceProvider provider, string name, Location location, List<ResourceProviderInfo> target)
        {
            var node = DescribeProvider(provider, name, location, visited, ancestors, budget);
            if (node is not null) target.Add(node);
        }
        void AddStyles(IStyle style, string path)
        {
            if (!budget.VisitNode()) return;
            if (!stylesVisited.Add(style)) { budget.Note("A shared or cyclic style scope was listed once."); return; }
            switch (style)
            {
                case Styles styles:
                    if (((IResourceNode)styles).HasResources)
                        AddProvider(styles.Resources, Clip(path, MaxKey), new(Clip(path + ".Resources", MaxText), "Styles", null), roots);
                    for (var i = 0; i < styles.Count && !budget.Truncated; i++) AddStyles(styles[i], Clip(path + "/" + i, MaxText));
                    break;
                case StyleBase item:
                    if (((IResourceNode)item).HasResources)
                        AddProvider(item.Resources, Clip(path, MaxKey), new(Clip(path + ".Resources", MaxText), "Style", null), roots);
                    for (var i = 0; i < item.Children.Count && !budget.Truncated; i++) AddStyles(item.Children[i], Clip(path + "/" + i, MaxText));
                    break;
                default:
                    budget.Note("A custom style provider cannot be enumerated through the public resource dictionary API.");
                    break;
            }
        }
    }

    private ResourceProviderInfo? DescribeProvider(IResourceProvider provider, string name, Location location,
        HashSet<IResourceProvider> visited, HashSet<IResourceProvider> ancestors, Budget budget)
    {
        if (!budget.VisitNode()) return null;
        var typeName = TypeName(provider);
        if (!budget.VisitOutput(name.Length + location.Path.Length + location.Scope.Length + (location.Theme?.Length ?? 0) + typeName.Length)) return null;
        var id = objects.GetId(provider);
        _locations.TryAdd(id, location);
        var children = new List<ResourceProviderInfo>();
        var cycle = ancestors.Contains(provider);
        var shared = !visited.Add(provider);
        if (provider is not IResourceDictionary dictionary)
        {
            budget.Note("Custom resource providers are shown with their type; their entries cannot be enumerated through the public API.");
            return new(id, name, location.Scope, location.Theme, location.Path, typeName, 0, false, shared, cycle, children);
        }
        if (cycle) budget.Note("A cyclic resource provider reference was stopped.");
        if (!shared && !cycle && ancestors.Count >= 16)
        {
            budget.Truncated = true;
            budget.Note("The resource hierarchy depth budget was reached.");
        }
        else if (!shared && !cycle)
        {
            ancestors.Add(provider);
            foreach (var child in ChildProviders(dictionary, location))
            {
                var node = DescribeProvider(child.Provider, child.Name, child.Location, visited, ancestors, budget);
                if (node is null) break;
                children.Add(node);
            }
            ancestors.Remove(provider);
        }
        return new(id, name, location.Scope, location.Theme, location.Path, typeName, dictionary.Count, true, shared, cycle, children);
    }

    public ResourceEntriesSnapshot QueryEntries(ResourceEntriesQueryRequest request)
    {
        ValidateBudget(request.MaxNodes, 2048, nameof(request.MaxNodes));
        ValidateBudget(request.MaxEntries, 20000, nameof(request.MaxEntries));
        if (request.Offset < 0 || request.Limit is < 1 or > 200 ||
            request.KeyFilter?.Length > MaxKey || request.TypeFilter?.Length > MaxKey || !Enum.IsDefined(request.Sort))
            throw new RpcException("invalid_query", "Offset must be nonnegative, limit 1..200, filters at most 256 characters and sort must be known.");
        var root = GetDictionary(request.DictionaryId);
        var location = LocationFor(request.DictionaryId);
        var budget = new Budget(request.MaxNodes, request.MaxEntries);
        var rows = new List<Candidate>();
        var ancestors = new HashSet<IResourceDictionary>(ReferenceEqualityComparer.Instance);
        Visit(root, location);
        var ordered = request.Sort switch
        {
            ResourceQuerySort.Type => rows.OrderBy(row => row.Type, StringComparer.Ordinal).ThenBy(row => row.Key as string ?? row.KeyText, StringComparer.Ordinal),
            ResourceQuerySort.Source => rows.OrderBy(row => row.Location.Path, StringComparer.Ordinal).ThenBy(row => row.Key as string ?? row.KeyText, StringComparer.Ordinal),
            _ => rows.OrderBy(row => row.Key as string ?? row.KeyText, StringComparer.Ordinal).ThenBy(row => row.Location.Path, StringComparer.Ordinal)
        };
        IEnumerable<Candidate> sorted = ordered.ThenBy(row => row.KeyId);
        if (request.Descending) sorted = sorted.Reverse();
        if (request.Offset > rows.Count)
            throw new RpcException("invalid_offset", "The page start is past the available query results. Refresh from offset zero.");
        var page = sorted.Skip(request.Offset).Take(request.Limit).Select(DescribeEntry).ToArray();
        return new(request.DictionaryId, request.Offset, rows.Count, page, request.Offset + page.Length < rows.Count,
            budget.Truncated, budget.Nodes, budget.Entries, budget.Notes.ToArray());

        void Visit(IResourceDictionary dictionary, Location source)
        {
            if (!budget.VisitNode()) return;
            if (ancestors.Count >= 16) { budget.Truncated = true; budget.Note("The resource hierarchy depth budget was reached."); return; }
            if (!ancestors.Add(dictionary)) { budget.Note("A cyclic resource provider reference was stopped."); return; }
            foreach (var key in dictionary.Keys)
            {
                if (!budget.VisitEntry()) break;
                var keyText = KeyText(key);
                var value = dictionary[key];
                var type = value is null ? "null" : value.GetType().FullName ?? value.GetType().Name;
                if ((string.IsNullOrEmpty(request.KeyFilter) || key is string text && text.Contains(request.KeyFilter, StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrEmpty(request.TypeFilter) || type.Contains(request.TypeFilter, StringComparison.OrdinalIgnoreCase)))
                    rows.Add(new(dictionary, key, objects.GetId(key), keyText, value, type, source));
            }
            if (request.IncludeNested && !budget.Truncated)
                foreach (var child in ChildProviders(dictionary, source))
                {
                    if (child.Provider is IResourceDictionary nested) Visit(nested, child.Location);
                    else
                    {
                        if (!budget.VisitNode()) break;
                        budget.Note("Custom resource provider entries cannot be enumerated through the public API.");
                    }
                    if (budget.Truncated) break;
                }
            ancestors.Remove(dictionary);
        }
    }

    public ResourceQueryEntryInfo ReadEntry(ResourceReadEntryRequest request)
    {
        var dictionary = GetDictionary(request.DictionaryId);
        var key = objects.Get(request.KeyObjectId);
        if (!dictionary.ContainsKey(key)) throw new RpcException("resource_not_found", "The key is no longer present in its original dictionary.");
        var value = dictionary[key];
        return DescribeEntry(new(dictionary, key, request.KeyObjectId, KeyText(key), value,
            value is null ? "null" : TypeName(value), LocationFor(request.DictionaryId)));
    }

    private ResourceQueryEntryInfo DescribeEntry(Candidate row)
    {
        var ownerId = objects.GetId(row.Dictionary);
        var stringKey = row.Key as string;
        var keyTruncated = stringKey?.Length > MaxKey;
        if (keyTruncated) stringKey = null;
        var preview = ValueText(row.Value);
        return new(ownerId, row.Location.Path, row.Location.Scope, row.Location.Theme, row.KeyText,
            TypeName(row.Key), stringKey, row.KeyId, Clip(row.Type, MaxKey), ClipNullable(preview, MaxText),
            row.Value is null || row.Value.GetType().IsValueType ? null : objects.GetId(row.Value),
            stringKey is not null && row.Value is not null && preview?.Length <= MaxText && !row.Dictionary.IsReadOnly && PropertyValues.CanConvert(row.Value.GetType()),
            row.Key is string key && _edits.TryGetValue((ownerId, key), out var edit) && IsOurs(row.Value, edit.Modified),
            keyTruncated, preview?.Length > MaxText);
    }

    private IEnumerable<ChildProvider> ChildProviders(IResourceDictionary dictionary, Location source)
    {
        for (var i = 0; i < dictionary.MergedDictionaries.Count; i++)
            yield return new(dictionary.MergedDictionaries[i], "Merged " + i,
                source with { Path = Clip(source.Path + "/Merged[" + i + "]", MaxText) });
        var index = 0;
        foreach (var theme in dictionary.ThemeDictionaries)
        {
            var name = theme.Key is ThemeVariant variant && variant.Key is string text ? Clip(text, MaxKey) : KeyText(theme.Key);
            yield return new(theme.Value, "Theme " + name,
                source with { Path = Clip(source.Path + "/Theme[" + index++ + ":" + name + "]", MaxText), Theme = name });
        }
    }

    private Location LocationFor(long id) => _locations.TryGetValue(id, out var location) ? location : new("Dictionary #" + id, "Dictionary", null);
    private string KeyText(object key) => key is string text ? Clip(text, MaxKey) : "[" + TypeName(key) + " #" + objects.GetId(key) + "]";
    private static string TypeName(object value) => Clip(value.GetType().FullName ?? value.GetType().Name, MaxKey);
    private static string? ValueText(object? value) => value switch
    {
        null => null,
        string text => text,
        _ when PropertyValues.CanConvert(value.GetType()) && (value.GetType().IsPrimitive || value.GetType().IsEnum ||
            value is decimal || value.GetType().Assembly == typeof(AvaloniaObject).Assembly) => PropertyValues.Format(value),
        _ => "[" + TypeName(value) + "]"
    };
    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)] + "…";
    private static string? ClipNullable(string? text, int length) => text is null ? null : Clip(text, length);
    private static void ValidateBudget(int value, int maximum, string name)
    {
        if (value < 1 || value > maximum) throw new RpcException("invalid_query", $"{name} must be between 1 and {maximum}.");
    }
    private sealed record Location(string Path, string Scope, string? Theme);
    private sealed record ChildProvider(IResourceProvider Provider, string Name, Location Location);
    private sealed record Candidate(IResourceDictionary Dictionary, object Key, long KeyId, string KeyText, object? Value, string Type, Location Location);
    private sealed class Budget(int maxNodes, int maxEntries)
    {
        public int Nodes { get; private set; }
        public int Entries { get; private set; }
        public bool Truncated { get; set; }
        public HashSet<string> Notes { get; } = [];
        private int _outputCharacters;
        public bool VisitOutput(int characters)
        {
            // A UTF-16 unit needs at most six JSON bytes; reserve ample room for node scaffolding.
            if (_outputCharacters + characters <= 400000) { _outputCharacters += characters; return true; }
            Truncated = true;
            Note("The resource tree response text budget was reached.");
            return false;
        }
        public bool VisitNode() { if (Nodes < maxNodes) { Nodes++; return true; } Truncated = true; Note("The resource node budget was reached."); return false; }
        public bool VisitEntry() { if (Entries < maxEntries) { Entries++; return true; } Truncated = true; Note("The resource entry budget was reached; totals cover only visited entries."); return false; }
        public void Note(string text) => Notes.Add(text);
    }

    public IReadOnlyList<ResourceDictionaryInfo> GetTrees(ResourcesRequest request)
    {
        return GetTree(new ResourceTreeQueryRequest(request.ObjectId, 1024)).Roots.Where(node => node.CanEnumerate).Select(Convert).ToArray();
        static ResourceDictionaryInfo Convert(ResourceProviderInfo node) => new(node.Id, node.Name, node.Scope, node.Theme,
            node.EntryCount, node.Children.Where(child => child.CanEnumerate).Select(Convert).ToArray());
    }

    public IReadOnlyList<ResourceEntryInfo> GetEntries(long dictionaryId)
    {
        var dictionary = GetDictionary(dictionaryId);
        return dictionary.Keys.Take(1000).Select(key =>
        {
            var value = dictionary[key];
            var editable = key is string && value is not null && !dictionary.IsReadOnly && PropertyValues.CanConvert(value.GetType());
            var overridden = key is string text && _edits.TryGetValue((dictionaryId, text), out var edit) && IsOurs(value, edit.Modified);
            var preview = ValueText(value);
            return new ResourceEntryInfo(KeyText(key), TypeName(key), value is null ? "null" : TypeName(value),
                ClipNullable(preview, MaxText), editable && preview?.Length <= MaxText && key is string { Length: <= MaxKey }, overridden);
        }).OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray();
    }

    public OperationResult Set(SetResourceRequest request)
    {
        var dictionary = GetDictionary(request.DictionaryId);
        if (!dictionary.ContainsKey(request.Key) || !dictionary.TryGetValue(request.Key, out var current))
            throw new RpcException("resource_not_found", "The resource key is not present in this dictionary.");
        if (current is null || dictionary.IsReadOnly || !PropertyValues.CanConvert(current.GetType()))
            throw new RpcException("unsupported_type", "The resource type has no safe text editor.");
        object? replacement;
        try { replacement = PropertyValues.Parse(request.Value, current.GetType()); }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException or NotSupportedException)
        { throw new RpcException("conversion_failed", exception.Message); }
        var key = (request.DictionaryId, request.Key);
        if (_edits.TryGetValue(key, out var old) && !IsOurs(current, old.Modified))
            throw new RpcException("resource_conflict", "The application changed this resource after the diagnostic edit. Reset the edit before retrying.");
        var original = old is null ? current : old.Original;
        dictionary[request.Key] = replacement;
        _edits[key] = new ResourceEdit(new WeakReference<IResourceDictionary>(dictionary), original, replacement);
        return new OperationResult();
    }

    public OperationResult Reset(ResetResourceRequest request)
    {
        var dictionary = GetDictionary(request.DictionaryId);
        if (!_edits.Remove((request.DictionaryId, request.Key), out var edit))
            return new OperationResult();
        if (!dictionary.ContainsKey(request.Key) || !dictionary.TryGetValue(request.Key, out var current) || !IsOurs(current, edit.Modified))
            throw new RpcException("resource_conflict", "The application's newer resource value was preserved.");
        dictionary[request.Key] = edit.Original;
        return new OperationResult();
    }

    private IResourceDictionary GetDictionary(long id) => objects.Get(id) as IResourceDictionary
        ?? throw new RpcException("unsupported_object", "The object is not a resource dictionary.");

    private static bool IsOurs(object? current, object? modified) => ReferenceEquals(current, modified)
        || (current?.GetType().IsValueType == true && Equals(current, modified));

    public void Dispose()
    {
        foreach (var pair in _edits)
        {
            if (pair.Value.Dictionary.TryGetTarget(out var dictionary) && !dictionary.IsReadOnly &&
                dictionary.ContainsKey(pair.Key.Key) && dictionary.TryGetValue(pair.Key.Key, out var current) && IsOurs(current, pair.Value.Modified))
                dictionary[pair.Key.Key] = pair.Value.Original;
        }
        _edits.Clear();
        _locations.Clear();
    }

    private sealed record ResourceEdit(WeakReference<IResourceDictionary> Dictionary, object? Original, object? Modified);
}

