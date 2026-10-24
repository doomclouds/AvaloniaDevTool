using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private bool _includeNestedResources;
    private string _resourceKeyFilter = "";
    private string _resourceTypeFilter = "";
    private ResourceQuerySort _resourceSort;
    private bool _resourceSortDescending;
    private long? _resourceQueryDictionaryId;
    private int _resourceOffset;
    private ResourceEntriesSnapshot? _resourcePage;
    private string _resourceTreeNotes = "";
    private readonly Dictionary<long, ResourceProviderInfo> _resourceProviders = [];
    private const int ResourcePageSize = 50;

    public bool IncludeNestedResources
    {
        get => _includeNestedResources;
        set { if (Set(ref _includeNestedResources, value)) ResourceQueryChanged(); }
    }
    public string ResourceKeyFilter
    {
        get => _resourceKeyFilter;
        set { if (Set(ref _resourceKeyFilter, value ?? "")) ResourceQueryChanged(); }
    }
    public string ResourceTypeFilter
    {
        get => _resourceTypeFilter;
        set { if (Set(ref _resourceTypeFilter, value ?? "")) ResourceQueryChanged(); }
    }
    public IReadOnlyList<ResourceQuerySort> ResourceSortChoices { get; } = Enum.GetValues<ResourceQuerySort>();
    public ResourceQuerySort ResourceSort
    {
        get => _resourceSort;
        set { if (Set(ref _resourceSort, value)) ResourceQueryChanged(); }
    }
    public bool ResourceSortDescending
    {
        get => _resourceSortDescending;
        set { if (Set(ref _resourceSortDescending, value)) ResourceQueryChanged(); }
    }
    public string ResourcePageSummary => _resourcePage is { } page
        ? $"{(page.TotalCount == 0 ? 0 : page.Offset + 1)}–{page.Offset + page.Entries.Count} / {page.TotalCount} 项" +
          (page.Truncated ? "（扫描已截断）" : "")
        : "尚未查询";
    public AsyncCommand PreviousResourcePageCommand { get; private set; } = null!;
    public AsyncCommand NextResourcePageCommand { get; private set; } = null!;

    private void InitializeResources()
    {
        PreviousResourcePageCommand = new AsyncCommand(() => MoveResourcePageAsync(-1), ReportError,
            () => CanUseNativeTools && _resourcePage is { Offset: > 0 });
        NextResourcePageCommand = new AsyncCommand(() => MoveResourcePageAsync(1), ReportError,
            () => CanUseNativeTools && _resourcePage is { HasMore: true });
    }

    private void RefreshResourceCommands()
    {
        PreviousResourcePageCommand?.Refresh();
        NextResourcePageCommand?.Refresh();
    }

    private void ResourceQueryChanged()
    {
        _resourceOffset = 0;
        _resourcePage = null;
        Raise(nameof(ResourcePageSummary));
        RefreshResourceCommands();
        if (!CanUseNativeTools || SelectedResourceDictionary is not { } dictionary) return;
        _ = RunSafelyAsync(async () =>
        {
            var (generation, token) = BeginInspectionLoad();
            await LoadResourceQueryEntriesAsync(dictionary, generation, token);
        });
    }

    private async Task MoveResourcePageAsync(int direction)
    {
        if (!CanUseNativeTools || SelectedResourceDictionary is not { } dictionary || _resourcePage is not { } page) return;
        _resourceOffset = Math.Max(0, page.Offset + direction * ResourcePageSize);
        var (generation, token) = BeginInspectionLoad();
        await LoadResourceQueryEntriesAsync(dictionary, generation, token);
    }

    private async Task RefreshResourcesAsync(int generation, CancellationToken token)
    {
        var tree = await _client.QueryResourceTreeAsync(new(SelectedNode?.Id), token);
        if (!IsCurrentInspection(generation, token)) return;
        var previousId = SelectedResourceDictionary?.Id;
        _resourceProviders.Clear();
        var resources = tree.Roots.Select(Convert).ToArray();
        _resourceTreeNotes = (tree.Truncated ? "资源作用域树已截断。 " : "") + string.Join(" ", tree.Limitations);
        Replace(ResourceDictionaries, resources);
        _selectedDictionary = FlattenResources(resources).FirstOrDefault(dictionary => dictionary.Id == previousId) ?? resources.FirstOrDefault();
        Raise(nameof(SelectedResourceDictionary));
        _resourceOffset = 0;
        if (_selectedDictionary is { } dictionary) await LoadResourceQueryEntriesAsync(dictionary, generation, token);
        else { ResetResourceQuery(); ResourceSummary = "没有可枚举的资源字典。 " + _resourceTreeNotes; }

        ResourceDictionaryInfo Convert(ResourceProviderInfo node)
        {
            _resourceProviders.TryAdd(node.Id, node);
            return new(node.Id,
                node.Name + (!node.CanEnumerate ? $"（不可枚举：{node.TypeName}）" : node.IsCycle ? "（循环引用）" : node.IsReference ? "（共享）" : ""),
                node.Scope, node.Theme, node.EntryCount, node.Children.Select(Convert).ToArray());
        }
    }

    private async Task LoadResourceQueryEntriesAsync(ResourceDictionaryInfo dictionary, int generation, CancellationToken token)
    {
        if (_resourceQueryDictionaryId != dictionary.Id)
        {
            _resourceQueryDictionaryId = dictionary.Id;
            _resourceOffset = 0;
            _resourcePage = null;
            ResourceEntries.Clear();
            Raise(nameof(ResourcePageSummary));
            RefreshResourceCommands();
        }
        if (_resourceProviders.TryGetValue(dictionary.Id, out var provider) && !provider.CanEnumerate)
        {
            ResourceEntries.Clear();
            ResourceSummary = $"{provider.Path} · {provider.TypeName}：公开 API 无法枚举此自定义资源提供者，不能据此判断为空。";
            return;
        }
        var page = await _client.QueryResourceEntriesAsync(new(dictionary.Id, IncludeNestedResources,
            ResourceKeyFilter, ResourceTypeFilter, ResourceSort, ResourceSortDescending, _resourceOffset, ResourcePageSize), token);
        if (!IsCurrentInspection(generation, token) || SelectedResourceDictionary?.Id != dictionary.Id) return;
        var existing = ResourceEntries.ToArray();
        ResourceEntries.Clear();
        foreach (var entry in page.Entries)
        {
            var row = existing.FirstOrDefault(item => item.DictionaryId == entry.OwnerDictionaryId &&
                item.KeyObjectId == entry.KeyObjectId && item.ProviderPath == entry.ProviderPath);
            if (row is null) row = new ResourceEntryViewModel(entry, ApplyResourceQueryAsync, ResetResourceQueryAsync, ReadResourceQueryEntryAsync);
            else row.Refresh(entry);
            ResourceEntries.Add(row);
        }
        _resourcePage = page;
        Raise(nameof(ResourcePageSummary));
        RefreshResourceCommands();
        ResourceSummary = $"{dictionary.Name} · {(IncludeNestedResources ? "含合并与主题字典" : "仅此字典")} · " +
            $"扫描 {page.NodesVisited} 个作用域、{page.EntriesVisited} 项。修改作用于条目来源字典，恢复时保留应用的新值。" +
            (page.Truncated ? " 预算已到达，总数仅含已扫描条目。" : "") +
            (_resourceTreeNotes.Length > 0 ? "\n" + _resourceTreeNotes : "") +
            (page.Limitations.Count > 0 ? "\n" + string.Join(" ", page.Limitations) : "");
    }

    private async Task ApplyResourceQueryAsync(ResourceEntryViewModel row)
    {
        if (!CanUseNativeTools || !ResourceEntries.Contains(row) || row.StringKey is not { } key) return;
        var generation = _inspectionGeneration;
        var token = _inspectionCancellation?.Token ?? _lifetime.Token;
        await _client.SetResourceAsync(row.DictionaryId, key, row.Draft, token);
        if (!IsCurrentInspection(generation, token) || SelectedResourceDictionary is not { } dictionary) return;
        await LoadResourceQueryEntriesAsync(dictionary, generation, token);
        if (IsCurrentInspection(generation, token)) Status = $"已临时修改资源 {row.Key}，可恢复原值。";
    }

    private async Task ReadResourceQueryEntryAsync(ResourceEntryViewModel row)
    {
        if (!CanUseNativeTools || !ResourceEntries.Contains(row)) return;
        var generation = _inspectionGeneration;
        var token = _inspectionCancellation?.Token ?? _lifetime.Token;
        var entry = await _client.ReadResourceEntryAsync(new(row.DictionaryId, row.KeyObjectId), token);
        if (IsCurrentInspection(generation, token) && ResourceEntries.Contains(row)) row.Refresh(entry with { ProviderPath = row.ProviderPath });
    }

    private async Task ResetResourceQueryAsync(ResourceEntryViewModel row)
    {
        if (!CanUseNativeTools || !ResourceEntries.Contains(row) || row.StringKey is not { } key) return;
        var generation = _inspectionGeneration;
        var token = _inspectionCancellation?.Token ?? _lifetime.Token;
        try { await _client.ResetResourceAsync(row.DictionaryId, key, token); }
        finally
        {
            if (IsCurrentInspection(generation, token) && SelectedResourceDictionary is { } dictionary)
                await LoadResourceQueryEntriesAsync(dictionary, generation, token);
        }
        if (IsCurrentInspection(generation, token)) Status = $"已恢复资源 {row.Key}。";
    }

    private void ResetResourceQuery()
    {
        _resourceQueryDictionaryId = null;
        _resourceOffset = 0;
        _resourcePage = null;
        _resourceTreeNotes = "";
        _resourceProviders.Clear();
        Raise(nameof(ResourcePageSummary));
        RefreshResourceCommands();
    }

    private void DisposeResources() => ResetResourceQuery();
}
