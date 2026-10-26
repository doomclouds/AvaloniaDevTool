using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private readonly Dictionary<(long ObjectId, string Name), PseudoClassOverride> _explorationPseudoClasses = [];

    private object HandleExploration(string method, JsonElement parameters) => method switch
    {
        OfficialTreeMethods.Snapshot => GetOfficialTree(ProtocolJson.Read<OfficialTreeRequest>(parameters)),
        OfficialTreeMethods.Ancestors => GetOfficialAncestors(ProtocolJson.Read<ObjectRequest>(parameters).ObjectId),
        OfficialTreeMethods.Search => SearchOfficialTree(ProtocolJson.Read<OfficialTreeSearchRequest>(parameters)),
        ExplorationMethods.Ancestors => GetAncestors(ProtocolJson.Read<TreeAncestorsRequest>(parameters)),
        ExplorationMethods.Search => SearchTree(ProtocolJson.Read<TreeSearchRequest>(parameters)),
        ExplorationMethods.Action => PerformElementAction(ProtocolJson.Read<ElementActionRequest>(parameters)),
        ExplorationMethods.PseudoClasses => GetPseudoClasses(ProtocolJson.Read<ObjectRequest>(parameters).ObjectId),
        ExplorationMethods.SetPseudoClass => SetPseudoClass(ProtocolJson.Read<SetPseudoClassRequest>(parameters)),
        ExplorationMethods.ResetPseudoClass => ResetPseudoClass(ProtocolJson.Read<ResetPseudoClassRequest>(parameters)),
        ExplorationMethods.Screenshot => CaptureElement(ProtocolJson.Read<CaptureElementRequest>(parameters)),
        _ => throw new RpcException("method_not_found", $"Unknown method: {method}")
    };

    private IReadOnlyList<TreeNodeInfo> GetAncestors(TreeAncestorsRequest request)
    {
        ValidateTreeKind(request.Kind);
        var target = _objects.Get(request.ObjectId);
        var roots = RefreshRoots();
        if (request.Kind == TreeKind.Merged)
            return MergedPath(target, roots).Select(item => Describe(item, request.Kind)).ToArray();
        var path = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object? current = target;
        while (current is not null && path.Count < 512)
        {
            if (!seen.Add(current))
                throw new RpcException("invalid_tree", "The ancestor chain contains a cycle.");
            path.Add(current);
            if (roots.Any(root => ReferenceEquals(root, current)))
            {
                path.Reverse();
                return path.Select(item => Describe(item, request.Kind)).ToArray();
            }

            current = request.Kind switch
            {
                TreeKind.Visual when current is Visual visual => visual.GetVisualParent(),
                TreeKind.Logical when current is ILogical logical => logical.LogicalParent,
                _ => null
            };
        }

        if (path.Count >= 512)
            throw new RpcException("tree_limit", "The ancestor chain exceeds the 512-node safety limit.");
        throw new RpcException("object_detached", "The object is not reachable from a current root in this tree.");
    }

    private TreeSearchResult SearchTree(TreeSearchRequest request)
    {
        ValidateTreeKind(request.Kind);
        if (request.MaxResults is < 1 or > 500 || request.MaxVisited is < 1 or > 20000)
            throw new RpcException("invalid_request", "MaxResults must be 1..500 and MaxVisited must be 1..20000.");
        if (request.Query is null || request.Query.Length > 1024)
            throw new RpcException("invalid_request", "The query must contain at most 1024 characters.");
        var query = request.Query.Trim();
        var pending = new Queue<object>();
        var scheduled = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var matches = new List<TreeNodeInfo>();
        var visited = 0;
        var truncated = false;

        void Schedule(object item)
        {
            if (scheduled.Contains(item))
                return;
            if (scheduled.Count >= request.MaxVisited)
            {
                truncated = true;
                return;
            }

            scheduled.Add(item);
            pending.Enqueue(item);
        }

        if (request.RootId is { } rootId)
        {
            var root = _objects.Get(rootId);
            if (request.Kind == TreeKind.Merged)
                MergedPath(root, RefreshRoots());
            Schedule(root);
        }
        else
            foreach (var root in RefreshRoots())
                Schedule(root);

        while (pending.TryDequeue(out var current))
        {
            visited++;
            if (Matches(current, query))
            {
                var node = Describe(current, request.Kind);
                matches.Add(node);
                if (matches.Count == request.MaxResults)
                {
                    truncated |= pending.Count > 0 || node.HasChildren;
                    break;
                }
            }

            foreach (var child in ChildObjects(current, request.Kind))
                Schedule(child);
        }

        return new TreeSearchResult(matches, visited, truncated);
    }

    private static bool Matches(object value, string query)
    {
        if (query.Length == 0)
            return true;
        if (value is MergedTree.SyntheticNode synthetic &&
            synthetic.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        if ((value.GetType().FullName ?? value.GetType().Name).Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        if (value is StyledElement styled && styled.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return true;
        var preview = value switch
        {
            TextBlock text => text.Text,
            TextBox text => text.Text,
            ContentControl { Content: string text } => text,
            _ => null
        };
        return preview?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void ValidateTreeKind(TreeKind kind)
    {
        if (kind is not (TreeKind.Visual or TreeKind.Logical or TreeKind.Merged))
            throw new RpcException("invalid_request", "The requested tree kind is unsupported.");
    }

    private IReadOnlyList<object> MergedPath(object target, IReadOnlyList<TopLevel> roots)
    {
        var path = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object? current = target;
        while (current is not null && path.Count < 512)
        {
            if (!seen.Add(current))
                throw new RpcException("invalid_tree", "The merged ancestor chain contains a cycle.");
            if (current is MergedTree.SyntheticNode synthetic)
                synthetic.Validate();
            path.Add(current);
            if (roots.Any(root => ReferenceEquals(root, current)))
            {
                path.Reverse();
                return path;
            }

            current = _mergedTree.Parent(current);
        }

        if (path.Count >= 512)
            throw new RpcException("tree_limit", "The ancestor chain exceeds the 512-node safety limit.");
        throw new RpcException("object_detached",
            "The object is not reachable from a current root in the merged tree.");
    }

    private ElementActionResult PerformElementAction(ElementActionRequest request)
    {
        if (_objects.Get(request.ObjectId) is not Control control)
            throw new RpcException("unsupported_object", "Element actions require a control.");
        switch (request.Action)
        {
            case ElementAction.Focus:
                var focused = control.Focus();
                return new ElementActionResult(focused, focused ? null : "The control did not accept focus.");
            case ElementAction.BringIntoView:
                if (TopLevel.GetTopLevel(control) is null)
                    throw new RpcException("object_detached", "BringIntoView requires an attached control.");
                control.BringIntoView();
                return new ElementActionResult(true);
            case ElementAction.Enable:
            case ElementAction.Disable:
                SetProperty(new SetPropertyRequest(request.ObjectId,
                    PropertyId(InputElement.IsEnabledProperty),
                    request.Action == ElementAction.Enable ? "true" : "false"));
                return new ElementActionResult(true);
            default:
                throw new RpcException("invalid_request", "The requested element action is unsupported.");
        }
    }

    private IReadOnlyList<PseudoClassInfo> GetPseudoClasses(long objectId)
    {
        var target = GetStyledElement(objectId);
        var declared = target.GetType().GetCustomAttributes<PseudoClassesAttribute>(inherit: true)
            .SelectMany(attribute => attribute.PseudoClasses).ToHashSet(StringComparer.Ordinal);
        var active = target.Classes.Where(name => name.StartsWith(':')).ToHashSet(StringComparer.Ordinal);
        var touched = _explorationPseudoClasses.Keys.Where(key => key.ObjectId == objectId).Select(key => key.Name);
        return declared.Concat(active).Concat(touched).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(name => new PseudoClassInfo(name,
                active.Contains(name),
                declared.Contains(name),
                _explorationPseudoClasses.ContainsKey((objectId, name)))).ToArray();
    }

    private StyledElement GetStyledElement(long objectId) => _objects.Get(objectId) as StyledElement ??
                                                             throw new RpcException("unsupported_object",
                                                                 "Pseudo-classes require a StyledElement.");

    private static void ValidatePseudoClass(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length is < 2 or > 128 || name[0] != ':' ||
            name.Skip(1).Any(character => !char.IsLetterOrDigit(character) && character is not ('_' or '-')))
            throw new RpcException("invalid_request",
                "A pseudo-class must start with ':' and contain letters, digits, '_' or '-'.");
    }

    private OperationResult SetPseudoClass(SetPseudoClassRequest request)
    {
        ValidatePseudoClass(request.Name);
        var target = GetStyledElement(request.ObjectId);
        var key = (request.ObjectId, request.Name);
        _explorationPseudoClasses.TryAdd(key,
            new PseudoClassOverride(new WeakReference<StyledElement>(target), target.Classes.Contains(request.Name)));
        ((IPseudoClasses)target.Classes).Set(request.Name, request.Enabled);
        _pendingProperties.Add(request.ObjectId);
        return new OperationResult();
    }

    private OperationResult ResetPseudoClass(ResetPseudoClassRequest request)
    {
        ValidatePseudoClass(request.Name);
        _ = GetStyledElement(request.ObjectId);
        if (_explorationPseudoClasses.Remove((request.ObjectId, request.Name), out var entry) &&
            entry.Target.TryGetTarget(out var target))
            ((IPseudoClasses)target.Classes).Set(request.Name, entry.WasActive);
        _pendingProperties.Add(request.ObjectId);
        return new OperationResult();
    }

    private void DisposeExploration()
    {
        foreach (var entry in _explorationPseudoClasses)
            if (entry.Value.Target.TryGetTarget(out var target))
                ((IPseudoClasses)target.Classes).Set(entry.Key.Name, entry.Value.WasActive);
        _explorationPseudoClasses.Clear();
    }

    private sealed record PseudoClassOverride(WeakReference<StyledElement> Target, bool WasActive);
}
