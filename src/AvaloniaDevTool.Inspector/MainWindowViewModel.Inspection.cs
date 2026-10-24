using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private readonly List<AssetInfo> _allAssets = [];
    private readonly HashSet<long> _activitySequences = [];
    private CancellationTokenSource? _inspectionCancellation;
    private int _inspectionGeneration;
    private int _inspectionTabIndex;
    private ResourceDictionaryInfo? _selectedDictionary;
    private AssetInfo? _selectedAsset;
    private ActivityEntry? _selectedActivityEntry;
    private Bitmap? _assetImage;
    private string _styleSummary = "选择控件后查看样式与绑定。";
    private string _resourceSummary = "选择资源字典以查看条目。";
    private string _assetSummary = "刷新目标应用内嵌资产。";
    private string _assetText = "";
    private string _assetFilter = "";
    private string _activityEventName = "Click";
    private string _minimumLogLevel = "Warning";
    private bool _captureLogs = true;
    private bool _captureEvents;
    private bool _activitySelectedSourceOnly;
    private ActivityStatus? _activityStatus;

    public ObservableCollection<StyleFrameInfo> StyleFrames { get; } = [];
    public ObservableCollection<BindingDiagnosticInfo> BindingDiagnostics { get; } = [];
    public ObservableCollection<string> ValidationErrors { get; } = [];
    public ObservableCollection<ResourceDictionaryInfo> ResourceDictionaries { get; } = [];
    public ObservableCollection<ResourceEntryViewModel> ResourceEntries { get; } = [];
    public ObservableCollection<AssetInfo> FilteredAssets { get; } = [];
    public ObservableCollection<ActivityEntry> ActivityEntries { get; } = [];
    public IReadOnlyList<string> LogLevels { get; } = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal"];
    public AsyncCommand RefreshInspectionCommand { get; private set; } = null!;
    public AsyncCommand StartActivityCommand { get; private set; } = null!;
    public AsyncCommand StopActivityCommand { get; private set; } = null!;
    public AsyncCommand ClearActivityCommand { get; private set; } = null!;
    public string StyleSummary { get => _styleSummary; private set => Set(ref _styleSummary, value); }
    public string ResourceSummary { get => _resourceSummary; private set => Set(ref _resourceSummary, value); }
    public string AssetSummary { get => _assetSummary; private set => Set(ref _assetSummary, value); }
    public string AssetText { get => _assetText; private set => Set(ref _assetText, value); }
    public Bitmap? AssetImage { get => _assetImage; private set => Set(ref _assetImage, value); }
    public bool HasAssetImage => AssetImage is not null;
    public bool HasAssetText => AssetText.Length > 0;
    public string ActivitySummary => _activityStatus is { } state
        ? $"{(state.IsRunning ? "正在捕获" : "已停止")} · {ActivityEntries.Count} / 500 条 · 丢弃 {state.DroppedCount} 条 · " +
          (state.ObjectId is { } id ? $"事件源 #{id}" : "全局事件源") +
          (state.ByteBudget > 0 ? $"\n缓冲 {state.BufferedBytes:N0}/{state.ByteBudget:N0} 字节 · 字节预算淘汰 {state.ByteEvictedCount} · 内容预算截断 {state.BudgetTruncatedCount}" : "") +
          (state.NotificationByteBudget > 0 ? $" · 通知缓冲 {state.NotificationBufferedBytes:N0}/{state.NotificationByteBudget:N0} 字节 · 通知丢弃 {state.NotificationDroppedCount}" : "")
        : $"未开始捕获 · {ActivityEntries.Count} / 500 条";
    public bool CaptureLogs { get => _captureLogs; set => Set(ref _captureLogs, value); }
    public bool CaptureEvents { get => _captureEvents; set => Set(ref _captureEvents, value); }
    public bool ActivitySelectedSourceOnly { get => _activitySelectedSourceOnly; set => Set(ref _activitySelectedSourceOnly, value); }
    public string ActivityEventName { get => _activityEventName; set => Set(ref _activityEventName, value ?? ""); }
    public string MinimumLogLevel { get => _minimumLogLevel; set => Set(ref _minimumLogLevel, value ?? "Warning"); }
    public ActivityStatus? CurrentActivityStatus => _activityStatus;

    public int InspectionTabIndex
    {
        get => _inspectionTabIndex;
        set
        {
            if (Set(ref _inspectionTabIndex, value))
            {
                RuntimePageChanged();
                RefreshInspectionCommand?.Refresh();
                if (!_bindingNavigationChangingPage) _ = RunSafelyAsync(RefreshActiveInspectionAsync);
            }
        }
    }

    public ResourceDictionaryInfo? SelectedResourceDictionary
    {
        get => _selectedDictionary;
        set
        {
            if (Set(ref _selectedDictionary, value) && CanUseNativeTools && value is not null)
                _ = RunSafelyAsync(async () =>
                {
                    var (generation, token) = BeginInspectionLoad();
                    await LoadResourceEntriesAsync(value, generation, token);
                });
        }
    }

    public AssetInfo? SelectedAsset
    {
        get => _selectedAsset;
        set
        {
            if (!Set(ref _selectedAsset, value)) return;
            RefreshAssetCommands();
            ClearAssetPreview();
            if (value is null) { ClearAssetPreview(); return; }
            if (CanUseNativeTools)
                _ = RunSafelyAsync(async () =>
                {
                    var (generation, token) = BeginInspectionLoad();
                    await LoadAssetPreviewAsync(value, generation, token);
                });
        }
    }

    public string AssetFilter
    {
        get => _assetFilter;
        set { if (Set(ref _assetFilter, value ?? "")) FilterAssets(); }
    }

    public ActivityEntry? SelectedActivityEntry
    {
        get => _selectedActivityEntry;
        set
        {
            if (!Set(ref _selectedActivityEntry, value)) return;
            RefreshLogDetails();
            if (value?.ObjectId is { } id && CanUseNativeTools)
            {
                var generation = ++_pickGeneration;
                _ = RunSafelyAsync(() => LocatePickedAsync(id, generation));
            }
        }
    }

    private void InitializeInspection()
    {
        RefreshInspectionCommand = new AsyncCommand(RefreshActiveInspectionAsync, ReportError,
            () => IsConnected && (IsNativeBackend || InspectionTabIndex is 1 or 5) &&
                (!IsSyntheticSelection || InspectionTabIndex is 3 or 4 or 6 or 7 or 10));
        StartActivityCommand = new AsyncCommand(StartActivityAsync, ReportError, () => CanUseNativeTools && _activityStatus?.IsRunning != true);
        StopActivityCommand = new AsyncCommand(StopActivityAsync, ReportError, () => CanUseNativeTools && _activityStatus?.IsRunning == true);
        ClearActivityCommand = new AsyncCommand(ClearActivityAsync, ReportError, () => CanUseNativeTools);
        _client.ActivityEntryAdded += HandleActivityEntryAdded;
        _client.ActivityStateChanged += HandleActivityStateChanged;
        InitializeExploration();
        InitializeMetrics();
        InitializeBreakpoints();
        InitializeLayout();
        InitializeRuntime();
        InitializeBindingNavigation();
        InitializeMergedTree();
        InitializeProfiler();
        InitializeResources();
        InitializeComposition();
        InitializeLogs();
        InitializeOverlay();
        InitializeRendererDiagnostics();
        InitializeEventRoutes();
        InitializeAssets();
        InitializeProtocolMonitor();
        InitializePopupFreeze();
    }

    private void RefreshInspectionCommands()
    {
        RefreshInspectionCommand?.Refresh();
        StartActivityCommand?.Refresh();
        StopActivityCommand?.Refresh();
        ClearActivityCommand?.Refresh();
        RefreshExplorationCommands();
        RefreshMetricsCommands();
        RefreshBreakpointCommands();
        RefreshRuntimeCommands();
        RefreshBindingNavigationCommands();
        RefreshMergedCommands();
        RefreshProfilerCommands();
        RefreshResourceCommands();
        RefreshCompositionCommands();
        JumpToLogSourceCommand?.Refresh();
        RefreshOverlayCommands();
        RefreshRendererDiagnosticsCommands();
        RefreshEventRouteCommands();
        RefreshAssetCommands();
        RefreshProtocolTraceCommands();
        RefreshPopupFreezeCommands();
    }

    private void SelectionInspectionChanged()
    {
        OverlaySelectionChanged();
        RuntimeSelectionChanged();
        RefreshMergedCommands();
        CancelInspectionLoad();
        PseudoClasses.Clear();
        ClearElementImage();
        RefreshExplorationCommands();
        RefreshOverlayCommands();
        StyleFrames.Clear();
        ResourceDictionaries.Clear();
        ResourceEntries.Clear();
        ResetResourceQuery();
        _selectedDictionary = null;
        Raise(nameof(SelectedResourceDictionary));
        BindingDiagnostics.Clear();
        ValidationErrors.Clear();
        StyleSummary = "选择控件后查看样式与绑定。";
        SyncLayoutSelectionFromTree();
        SyncCompositionSelectionFromTree();
        RefreshInspectionCommand?.Refresh();
    }

    private (int Generation, CancellationToken Token) BeginInspectionLoad()
    {
        CancelInspectionLoad();
        _inspectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        return (_inspectionGeneration, _inspectionCancellation.Token);
    }

    private void CancelInspectionLoad()
    {
        _inspectionGeneration++;
        _inspectionCancellation?.Cancel();
        _inspectionCancellation?.Dispose();
        _inspectionCancellation = null;
    }

    private bool IsCurrentInspection(int generation, CancellationToken token) =>
        !token.IsCancellationRequested && generation == _inspectionGeneration && IsConnected && !_disposed;

    public async Task RefreshActiveInspectionAsync()
    {
        if (!IsConnected || InspectionTabIndex == 0)
            return;
        if (IsSyntheticSelection && InspectionTabIndex is 1 or 2 or 5 or 8 or 9)
        {
            Status = SyntheticSelectionNotice;
            return;
        }
        if (!IsNativeBackend && InspectionTabIndex is not (1 or 5))
        {
            Status = "当前后端仅提供只读树、属性、样式和截图；此高级功能未支持。";
            return;
        }
        var (generation, token) = BeginInspectionLoad();
        switch (InspectionTabIndex)
        {
            case 1:
                if (SelectedNode is not { IsPlaceholder: false } node)
                {
                    StyleSummary = "请先在控件树中选择一个对象。";
                    return;
                }
                var snapshot = await _backend.GetStylesAsync(node.Id, token);
                if (!IsCurrentInspection(generation, token)) return;
                Replace(StyleFrames, snapshot.Frames);
                Replace(BindingDiagnostics, snapshot.Bindings);
                Replace(ValidationErrors, snapshot.ValidationErrors);
                StyleSummary = $"{snapshot.Frames.Count} 个值帧 · {snapshot.Bindings.Count} 个绑定 · {snapshot.Adapter}" +
                    (snapshot.Truncated ? " · 结果已截断" : "") +
                    (snapshot.Limitations is { Count: > 0 } notes ? "\n" + string.Join("\n", notes) : "");
                break;
            case 2:
                await RefreshResourcesAsync(generation, token);
                if (IsCurrentInspection(generation, token)) await RefreshBindingResourceMatchesAsync(generation, token);
                break;
            case 3:
                var assets = await _client.GetAssetsAsync(token);
                if (!IsCurrentInspection(generation, token)) return;
                _allAssets.Clear();
                _allAssets.AddRange(assets);
                FilterAssets();
                AssetSummary = $"{assets.Count} 项内嵌资产 · 预览最多 1 MiB，完整导出最多 100 MiB";
                break;
            case 4:
                var history = await _client.GetActivityAsync(token);
                var status = await _client.GetActivityStatusAsync(token);
                if (!IsCurrentInspection(generation, token)) return;
                ApplyActivitySnapshot(history, status);
                await RefreshEventRoutesAsync();
                break;
            case 5:
                await RefreshExplorationPanelAsync(generation, token);
                if (CanUseNativeTools)
                {
                    await RefreshOverlayAsync();
                    await RefreshRendererDiagnosticsAsync();
                }
                break;
            case 6:
                await RefreshMetricsAsync();
                break;
            case 7:
                UpdateBreakpointMembers();
                await RefreshBreakpointsAsync();
                break;
            case 8:
                if (LayerSceneMode == 1) await RefreshCompositionAsync();
                else await RefreshLayoutAsync(generation, token);
                break;
            case 9:
                await RefreshRuntimeAsync();
                break;
            case 10:
                await RefreshProfilerAsync();
                break;
        }
    }

    private async Task LoadResourceEntriesAsync(ResourceDictionaryInfo dictionary, int generation, CancellationToken token)
    {
        await LoadResourceQueryEntriesAsync(dictionary, generation, token);
    }

    private async Task ApplyResourceAsync(ResourceEntryViewModel row)
    {
        if (!IsConnected || SelectedResourceDictionary?.Id != row.DictionaryId) return;
        var token = _inspectionCancellation?.Token ?? _lifetime.Token;
        await _client.SetResourceAsync(row.DictionaryId, row.Key, row.Draft, token);
        await RefreshResourceRowsAsync(row.DictionaryId, token);
        Status = $"已临时修改资源 {row.Key}，可恢复原值。";
    }

    private async Task ResetResourceAsync(ResourceEntryViewModel row)
    {
        if (!IsConnected || SelectedResourceDictionary?.Id != row.DictionaryId) return;
        var token = _inspectionCancellation?.Token ?? _lifetime.Token;
        try { await _client.ResetResourceAsync(row.DictionaryId, row.Key, token); }
        finally { await RefreshResourceRowsAsync(row.DictionaryId, token); }
        Status = $"已恢复资源 {row.Key}。";
    }

    private async Task RefreshResourceRowsAsync(long dictionaryId, CancellationToken token)
    {
        var entries = await _client.GetResourceEntriesAsync(dictionaryId, token);
        token.ThrowIfCancellationRequested();
        if (SelectedResourceDictionary?.Id != dictionaryId) return;
        foreach (var entry in entries)
        {
            var existing = ResourceEntries.FirstOrDefault(row => row.Key == entry.Key && row.KeyType == entry.KeyType);
            if (existing is not null) existing.Refresh(entry);
            else ResourceEntries.Add(new ResourceEntryViewModel(dictionaryId, entry, ApplyResourceAsync, ResetResourceAsync));
        }
    }

    private static IEnumerable<ResourceDictionaryInfo> FlattenResources(IEnumerable<ResourceDictionaryInfo> source)
    {
        foreach (var item in source)
        {
            yield return item;
            foreach (var child in FlattenResources(item.Children)) yield return child;
        }
    }

    private void FilterAssets()
    {
        var filter = AssetFilter.Trim();
        Replace(FilteredAssets, _allAssets.Where(asset =>
            filter.Length == 0 || asset.Uri.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            asset.AssemblyName.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task LoadAssetPreviewAsync(AssetInfo asset, int generation, CancellationToken token)
    {
        var preview = await _client.OpenAssetAsync(asset.Uri, token);
        if (!IsCurrentInspection(generation, token) || SelectedAsset?.Uri != asset.Uri) return;
        ClearAssetPreview();
        ApplyAssetPreview(preview);
    }

    private void ClearAssetPreview()
    {
        var previous = AssetImage;
        AssetImage = null;
        previous?.Dispose();
        AssetText = "";
        ClearAssetMetadata();
        Raise(nameof(HasAssetImage));
        Raise(nameof(HasAssetText));
    }

    private async Task StartActivityAsync()
    {
        if (ActivitySelectedSourceOnly && !HasSelectedNativeElement())
            throw new RpcException("select_source", "请先选择要监听事件源的控件。");
        var request = new ActivityStartRequest(CaptureLogs, CaptureEvents,
            ActivitySelectedSourceOnly ? SelectedNode?.Id : null, ActivityEventName, MinimumLogLevel, CurrentLogFilter());
        await _client.StartActivityAsync(request, _lifetime.Token);
        SetActivityStatus(await _client.GetActivityStatusAsync(_lifetime.Token));
        Status = "活动捕获已开始。日志在整个应用中采集，事件可按原始来源对象筛选。";
    }

    private async Task StopActivityAsync()
    {
        await _client.StopActivityAsync(_lifetime.Token);
        SetActivityStatus(await _client.GetActivityStatusAsync(_lifetime.Token));
        Status = "活动捕获已停止，已记录内容保留。";
        await RefreshEventRoutesAsync();
    }

    private async Task ClearActivityAsync()
    {
        CancelInspectionLoad();
        await _client.ClearActivityAsync(_lifetime.Token);
        ActivityEntries.Clear();
        ResetEventRouteRows();
        _activitySequences.Clear();
        SelectedActivityEntry = null;
        SetActivityStatus(await _client.GetActivityStatusAsync(_lifetime.Token));
    }

    private void HandleActivityEntryAdded(ActivityEntry entry)
    {
        var sessionId = _backend.Session?.SessionId;
        Dispatch(() =>
        {
            if (IsConnected && _backend.Session?.SessionId == sessionId)
                AddActivityEntry(entry);
        });
    }

    private void HandleActivityStateChanged(ActivityStatus state)
    {
        var sessionId = _backend.Session?.SessionId;
        Dispatch(() =>
        {
            if (IsConnected && _backend.Session?.SessionId == sessionId)
                SetActivityStatus(state);
        });
    }

    private void AddActivityEntry(ActivityEntry entry)
    {
        if (!_activitySequences.Add(entry.Sequence)) return;
        while (ActivityEntries.Count >= 500)
        {
            _activitySequences.Remove(ActivityEntries[0].Sequence);
            ActivityEntries.RemoveAt(0);
        }
        ActivityEntries.Add(entry);
        Raise(nameof(ActivitySummary));
    }

    private void SetActivityStatus(ActivityStatus state)
    {
        _activityStatus = state;
        Raise(nameof(CurrentActivityStatus));
        Raise(nameof(ActivitySummary));
        RefreshInspectionCommands();
    }

    private void ResetInspectionState()
    {
        CancelAssetExport();
        CancelInspectionLoad();
        StyleFrames.Clear();
        BindingDiagnostics.Clear();
        ValidationErrors.Clear();
        ResourceDictionaries.Clear();
        ResourceEntries.Clear();
        _selectedDictionary = null;
        Raise(nameof(SelectedResourceDictionary));
        _allAssets.Clear();
        FilteredAssets.Clear();
        _selectedAsset = null;
        Raise(nameof(SelectedAsset));
        ClearAssetPreview();
        ActivityEntries.Clear();
        _activitySequences.Clear();
        _selectedActivityEntry = null;
        RefreshLogDetails();
        _activityStatus = null;
        ResetExplorationState();
        ResetMetrics();
        ResetBreakpoints();
        ResetLayout();
        ResetRuntime();
        ResetProfiler();
        ResetOverlay();
        ResetRendererDiagnostics();
        ResetPopupFreeze();
        ResetEventRoutes();
        ResetResourceQuery();
        ResetComposition();
        Raise(nameof(SelectedActivityEntry));
        Raise(nameof(CurrentActivityStatus));
        Raise(nameof(ActivitySummary));
        StyleSummary = "选择控件后查看样式与绑定。";
        ResourceSummary = "连接应用后刷新资源。";
        AssetSummary = "连接应用后刷新内嵌资产。";
    }

    private void DisposeInspection()
    {
        DisposeProtocolMonitor();
        DisposePopupFreeze();
        CancelAssetExport();
        ResetRuntime();
        DisposeProfiler();
        DisposeOverlay();
        DisposeRendererDiagnostics();
        DisposeEventRoutes();
        DisposeResources();
        DisposeComposition();
        DisposeMetrics();
        DisposeBreakpoints();
        _client.ActivityEntryAdded -= HandleActivityEntryAdded;
        _client.ActivityStateChanged -= HandleActivityStateChanged;
        CancelInspectionLoad();
        ClearAssetPreview();
        ClearElementImage();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}

