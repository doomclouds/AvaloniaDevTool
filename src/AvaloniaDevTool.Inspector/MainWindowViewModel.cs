using System.Collections.ObjectModel;
using Avalonia.Threading;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DiagnosticsClient _client = new();
    private readonly IInspectionBackend _backend;
    private readonly string? _initialPipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _treeRefreshTimer;
    private readonly DispatcherTimer _propertyRefreshTimer;
    private readonly List<PropertyRowViewModel> _properties = [];
    private CancellationTokenSource? _treeCancellation;
    private CancellationTokenSource? _selectionCancellation;
    private string _pipeName;
    private string _status = "先启动测试应用，然后发现应用或粘贴管道名连接。";
    private string _propertyFilter = "";
    private TreeNodeViewModel? _selectedNode;
    private long? _desiredSelectionId;
    private bool _assigningSelection;
    private int _selectedTreeIndex;
    private int _treeGeneration;
    private TreeKind _loadedTreeKind;
    private int _pickGeneration;
    private bool _connecting;
    private bool _highlightEnabled = true;
    private bool _isPicking;
    private bool _restoringSelection;
    private bool _disposed;

    public MainWindowViewModel(string? initialPipeName = null, IInspectionBackend? backend = null, string? remoteBearerToken = null)
    {
        _backend = backend ?? _client;
        TreeKinds = IsNativeBackend ? ["视觉树 · Visual", "逻辑树 · Logical", "混合树 · Merged"] : ["视觉树 · Visual", "逻辑树 · Logical"];
        _highlightEnabled = IsNativeBackend;
        if (!IsNativeBackend) _status = "请提供已运行的 XamlMcp 目标 PID 或实例标识，使用共同只读能力。";
        _initialPipeName = initialPipeName;
        _pipeName = initialPipeName ?? "";
        _remoteBearerToken = remoteBearerToken ?? "";
        ConnectCommand = new AsyncCommand(ConnectAsync, ReportError, () => CanChangeConnection && !string.IsNullOrWhiteSpace(PipeName) &&
            (!IsRemoteEndpoint || !string.IsNullOrWhiteSpace(RemoteBearerToken)));
        DisconnectCommand = new AsyncCommand(DisconnectAsync, ReportError, () => IsConnected);
        DiscoverCommand = new AsyncCommand(DiscoverAsync, ReportError, () => IsNativeBackend && CanChangeConnection);
        RefreshCommand = new AsyncCommand(() => RefreshTreeAsync(true), ReportError, () => IsConnected);
        PickCommand = new AsyncCommand(TogglePickingAsync, ReportError, () => CanUseNativeTools);
        _treeRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _treeRefreshTimer.Tick += HandleTreeRefreshTick;
        _propertyRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _propertyRefreshTimer.Tick += HandlePropertyRefreshTick;
        _backend.ConnectionChanged += HandleConnectionChanged;
        _client.SelectionChanged += HandleSelectionChanged;
        _client.TreeInvalidated += HandleTreeInvalidated;
        _client.PropertiesChanged += HandlePropertiesChanged;
        _client.PickingChanged += HandlePickingChanged;
        InitializeInspection();
        InitializeTreeWorkflows();
    }

    public ObservableCollection<string> AvailablePipes { get; } = [];
    public ObservableCollection<TreeNodeViewModel> Roots { get; } = [];
    public ObservableCollection<PropertyRowViewModel> FilteredProperties { get; } = [];
    public IReadOnlyList<string> TreeKinds { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand DiscoverCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand PickCommand { get; }
    public bool IsConnected => _backend.IsConnected;
    public bool IsNativeBackend => ReferenceEquals(_backend, _client);
    public bool CanUseNativeTools => IsNativeBackend && IsConnected;
    public string BackendName => _backend.DisplayName;
    public string TreeLoadingHint => IsNativeBackend ? "展开节点按需加载；Merged 区分模板与用户内容" : "展开当前快照中的缓存子节点";
    public string EndpointLabel => IsNativeBackend ? "诊断地址" : "目标实例";
    public string EndpointPlaceholder => IsNativeBackend
        ? "输入本机管道名或 ws(s)://地址/路径"
        : "输入 PID、pid:12345 或 instance:实例标识";
    public string BackendModeNotice => IsNativeBackend
        ? IsRemoteEndpoint ? "Native WebSocket · 使用本次连接令牌；非回环地址需要受信任的 TLS。" : "Native 本机管道 · 支持可撤销编辑和实时通知"
        : $"{_backend.DisplayName} · 只读快照 · 请手动刷新；刷新会清除旧选择。" +
          (string.IsNullOrWhiteSpace(_backend.LastNotice) ? "" : " " + _backend.LastNotice);
    public bool CanChangeConnection => !IsConnected && !_connecting;
    public bool HasAvailablePipes => AvailablePipes.Count > 0;
    public bool ShowDiscoveredPipes => HasAvailablePipes && CanChangeConnection && !IsRemoteEndpoint;
    public string ConnectionBadge => _connecting ? "正在连接" : IsConnected ? "● 已连接" : "○ 尚未连接";
    public string SessionSummary => _backend.Session is { } session ? $"PID {session.ProcessId}  ·  Avalonia {session.AvaloniaVersion}" : _backend.DisplayName;
    public string SelectedTitle => SelectedNode is { IsPlaceholder: false } node ? node.TypeName + (node.Name.Length > 0 ? " " + node.Name : "") : "选择一个控件";
    public string SelectedSubtitle => SelectedNode is { IsPlaceholder: false } node
        ? node.IsSynthetic ? $"合成节点 #{node.Id}" + (node.Node.TemplateOwnerId is { } owner ? $" · 模板所属控件 #{owner}" : "") +
            (node.Node.SlotContentId is { } content ? $" · 实际内容 #{content}" : "")
        : $"{node.Node.TypeName}  ·  对象 #{node.Id}" + (node.RoleLabel.Length > 0 ? $" · {node.RoleLabel}" : "")
        : IsNativeBackend ? "在左侧选择，或开启拾取后点击测试应用中的控件。" : "在左侧展开快照并选择控件，目标变化后可手动刷新。";
    public string PickButtonText => _isPicking ? "取消拾取" : "点击拾取";
    public string PropertyCountText => $"{FilteredProperties.Count} / {_properties.Count} 项属性";
    public string TreeSummary => IsConnected ? $"{Roots.Count} 个窗口根 · {Roots.SelectMany(node => node.DescendantsAndSelf()).Count(node => !node.IsPlaceholder)} 个已加载节点" : "等待连接";

    public string PipeName
    {
        get => _pipeName;
        set
        {
            if (Set(ref _pipeName, value ?? ""))
            {
                RemoteBearerToken = "";
                Raise(nameof(IsRemoteEndpoint));
                Raise(nameof(ShowDiscoveredPipes));
                Raise(nameof(BackendModeNotice));
                ConnectCommand.Refresh();
            }
        }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }

    public string PropertyFilter
    {
        get => _propertyFilter;
        set
        {
            if (Set(ref _propertyFilter, value ?? ""))
                FilterProperties();
        }
    }

    public int SelectedTreeIndex
    {
        get => _selectedTreeIndex;
        set
        {
            if (value < 0 || value >= TreeKinds.Count) return;
            if (Set(ref _selectedTreeIndex, value))
            {
                ResetTreeSearch();
                if (IsConnected) _ = RunSafelyAsync(() => RefreshTreeAsync(false));
            }
        }
    }

    public TreeNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (_restoringSelection && value is null)
                return;
            if (!Set(ref _selectedNode, value))
                return;
            if (!_assigningSelection)
            {
                _desiredSelectionId = value?.IsPlaceholder == false ? value.Id : null;
                _pickGeneration++;
            }
            RefreshTreeWorkflowCommands();
            Raise(nameof(SelectedTitle));
            Raise(nameof(SelectedSubtitle));
            if (!_restoringSelection && !_assigningSelection)
                _ = RunSafelyAsync(() => SelectNodeAsync(value));
        }
    }

    public bool HighlightEnabled
    {
        get => _highlightEnabled;
        set
        {
            if (!IsNativeBackend) return;
            if (Set(ref _highlightEnabled, value) && CanUseNativeTools)
                _ = RunSafelyAsync(() => _client.HighlightAsync(value && SelectedNode is { IsSynthetic: false } node ? node.Id : null, _lifetime.Token));
        }
    }

    public Task InitializeAsync() => RunSafelyAsync(async () =>
    {
        if (IsNativeBackend) await DiscoverAsync();
        if (!string.IsNullOrWhiteSpace(_initialPipeName))
        {
            PipeName = _initialPipeName;
            if (IsRemoteEndpoint && string.IsNullOrWhiteSpace(RemoteBearerToken))
            {
                Status = "远程地址已填写，请输入本次连接令牌后连接。";
                return;
            }
            await ConnectAsync();
        }
    });

    private TreeKind CurrentTreeKind => SelectedTreeIndex switch
    {
        1 => TreeKind.Logical,
        2 when IsNativeBackend => TreeKind.Merged,
        _ => TreeKind.Visual
    };

    private async Task DiscoverAsync()
    {
        if (!IsNativeBackend) return;
        if (!OperatingSystem.IsWindows())
        {
            Status = "当前系统请从测试应用复制管道名；自动发现暂仅支持 Windows。";
            return;
        }
        var pipes = await Task.Run(() => Directory.GetFiles(@"\\.\pipe\")
            .Select(path => path[(path.LastIndexOf('\\') + 1)..])
            .Where(name => name.StartsWith(DiagnosticsProtocol.PipePrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), _lifetime.Token);
        AvailablePipes.Clear();
        foreach (var pipe in pipes)
            AvailablePipes.Add(pipe);
        Raise(nameof(HasAvailablePipes));
        Raise(nameof(ShowDiscoveredPipes));
        if (pipes.Length == 1 && string.IsNullOrWhiteSpace(PipeName))
            PipeName = pipes[0];
        Status = pipes.Length > 0 ? $"发现 {pipes.Length} 个本机应用，请选择管道后连接。" : "未发现应用。请先启动测试应用，或手动输入自定义管道名。";
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(PipeName))
            return;
        _connecting = true;
        RefreshConnectionState();
        Status = "正在连接目标应用…";
        try
        {
            if (IsRemoteEndpoint)
            {
                if (!Uri.TryCreate(PipeName.Trim(), UriKind.Absolute, out var endpoint))
                    throw new RpcException("invalid_endpoint", "请输入有效的 ws 或 wss 诊断地址。");
                await _client.ConnectRemoteAsync(endpoint, RemoteBearerToken, _lifetime.Token);
            }
            else await _backend.ConnectAsync(PipeName.Trim(), _lifetime.Token);
            await RefreshTreeAsync(false);
            Status = IsNativeBackend
                ? $"已连接 {_backend.Session?.ApplicationName}。选择控件可检查属性，修改在清除或断开后恢复。"
                : $"已连接 {_backend.Session?.ApplicationName}。只读快照：可查看树、属性、样式和截图，请手动刷新。";
            Raise(nameof(BackendModeNotice));
        }
        finally
        {
            _connecting = false;
            RefreshConnectionState();
        }
    }

    private async Task DisconnectAsync()
    {
        _treeCancellation?.Cancel();
        _selectionCancellation?.Cancel();
        _treeRefreshTimer.Stop();
        _propertyRefreshTimer.Stop();
        // Upstream MCP shutdown can outlive the moment its snapshot becomes invalid.
        if (!IsNativeBackend) ResetDisconnectedState();
        await _backend.DisconnectAsync();
        ResetDisconnectedState();
        Status = IsNativeBackend ? "已断开连接。目标应用中的高亮和诊断属性修改已释放。" : "已断开只读后端连接。";
    }

    private void RefreshConnectionState()
    {
        Raise(nameof(IsConnected));
        Raise(nameof(CanUseNativeTools));
        Raise(nameof(BackendModeNotice));
        Raise(nameof(CanChangeConnection));
        Raise(nameof(ShowDiscoveredPipes));
        Raise(nameof(ConnectionBadge));
        Raise(nameof(SessionSummary));
        ConnectCommand.Refresh();
        DisconnectCommand.Refresh();
        DiscoverCommand.Refresh();
        RefreshCommand.Refresh();
        PickCommand.Refresh();
        RefreshInspectionCommands();
        RefreshTreeWorkflowCommands();
    }

    private void ResetDisconnectedState()
    {
        _treeCancellation?.Cancel();
        _selectionCancellation?.Cancel();
        _treeRefreshTimer.Stop();
        _propertyRefreshTimer.Stop();
        _treeGeneration++;
        _pickGeneration++;
        _desiredSelectionId = null;
        ResetInspectionState();
        _restoringSelection = false;
        SelectedNode = null;
        Roots.Clear();
        ClearPropertyRows();
        FilterProperties();
        _isPicking = false;
        Raise(nameof(PickButtonText));
        Raise(nameof(TreeSummary));
        RefreshConnectionState();
    }

    private TreeNodeViewModel CreateNode(TreeNodeInfo info, int generation, TreeNodeViewModel? parent = null)
    {
        var node = new TreeNodeViewModel(info, generation, parent);
        node.Expanded = expanded => _ = RunSafelyAsync(() => LoadChildrenAsync(expanded, _treeCancellation?.Token ?? _lifetime.Token));
        node.Collapsed = collapsed => _ = RunSafelyAsync(() => CollapseSubtreeAsync(collapsed));
        return node;
    }

    private async Task RefreshTreeAsync(bool preserveState)
    {
        if (!IsConnected)
            return;
        if (!_backend.HasStableObjectIds)
        {
            // Snapshot backends invalidate their IDs when a new root snapshot is requested.
            preserveState = false;
            _selectionCancellation?.Cancel();
            _desiredSelectionId = null;
            CommitSelection(null);
            ClearPropertyRows();
            FilterProperties();
            SelectionInspectionChanged();
            Roots.Clear();
        }
        var expanded = preserveState
            ? Roots.SelectMany(node => node.DescendantsAndSelf()).Where(node => node.IsExpanded).Select(node => node.Id).ToHashSet()
            : [];
        if (!preserveState)
        {
            _desiredSelectionId = null;
            _pickGeneration++;
        }
        CancelTreeExpansion();
        await ReleaseLoadedTreeAsync(_loadedTreeKind, _lifetime.Token);
        _treeCancellation?.Cancel();
        _treeCancellation?.Dispose();
        _treeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellationToken = _treeCancellation.Token;
        var generation = ++_treeGeneration;
        var kind = CurrentTreeKind;
        _loadedTreeKind = kind;
        var roots = await _backend.GetRootsAsync(kind, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _restoringSelection = true;
        try
        {
            Roots.Clear();
            foreach (var root in roots)
                Roots.Add(CreateNode(root, generation));
            foreach (var root in Roots.ToArray())
            {
                if (!preserveState)
                {
                    root.IsExpanded = true;
                    await LoadChildrenAsync(root, cancellationToken);
                }
                else
                    await RestoreExpandedAsync(root, expanded, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            if (generation == _treeGeneration)
                _restoringSelection = false;
        }
        if (generation != _treeGeneration)
            return;
        var desiredId = _desiredSelectionId;
        var selection = desiredId is { } id ? FindLoadedNode(id) : null;
        if (desiredId is { } pendingId && selection is null && _backend.HasStableObjectIds &&
            generation == _treeGeneration && _desiredSelectionId == desiredId && IsConnected)
        {
            // A moved control may now belong to a collapsed branch. Keep its inspection until
            // navigation resolves the same stable identity instead of clearing drafts temporarily.
            var navigationGeneration = _pickGeneration;
            await LocatePickedAsync(pendingId, navigationGeneration);
            if (generation != _treeGeneration || navigationGeneration != _pickGeneration) return;
            if (_desiredSelectionId == desiredId && SelectedNode is { } stale && stale.Generation != generation)
            {
                CommitSelection(null);
                await SelectNodeAsync(null);
            }
        }
        else
        {
            CommitSelection(selection);
            await SelectNodeAsync(selection);
        }
        Raise(nameof(TreeSummary));
        Raise(nameof(BackendModeNotice));
    }

    private void CommitSelection(TreeNodeViewModel? node)
    {
        _assigningSelection = true;
        try { SelectedNode = node; }
        finally { _assigningSelection = false; }
    }

    private async Task RestoreExpandedAsync(TreeNodeViewModel node, HashSet<long> expanded, CancellationToken cancellationToken)
    {
        if (!expanded.Contains(node.Id))
            return;
        node.IsExpanded = true;
        await LoadChildrenAsync(node, cancellationToken);
        foreach (var child in node.Children.ToArray())
            await RestoreExpandedAsync(child, expanded, cancellationToken);
    }

    private TreeNodeViewModel? FindLoadedNode(long id) =>
        Roots.SelectMany(node => node.DescendantsAndSelf()).FirstOrDefault(node => node.Id == id);

    private async Task SelectNodeAsync(TreeNodeViewModel? node)
    {
        _selectionCancellation?.Cancel();
        _selectionCancellation?.Dispose();
        _selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellationToken = _selectionCancellation.Token;
        var preservePropertyRows = node is { IsPlaceholder: false, IsSynthetic: false } && node.Id == _propertyRowsObjectId;
        if (!preservePropertyRows) ClearPropertyRows();
        FilterProperties();
        SelectionInspectionChanged();
        var runtimeGeneration = _runtimeGeneration;
        if (!IsConnected)
            return;
        if (node is null || node.IsPlaceholder)
        {
            if (IsNativeBackend) await _client.HighlightAsync(null, cancellationToken);
            return;
        }
        if (node.IsSynthetic)
        {
            ResetLayout();
            if (IsNativeBackend) await _client.HighlightAsync(null, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (SelectedNode?.Id != node.Id) return;
            Status = SyntheticSelectionNotice;
            StyleSummary = SyntheticSelectionNotice;
            RefreshInspectionCommands();
        RefreshTreeWorkflowCommands();
            return;
        }
        await LoadPropertiesAsync(node, cancellationToken, preservePropertyRows);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsNativeBackend) await _client.HighlightAsync(HighlightEnabled ? node.Id : null, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedNode?.Id != node.Id) return;
        Status = IsNativeBackend
            ? $"已选择 {node.Description}。编辑后点击应用；清除可恢复原值来源。"
            : $"已选择 {node.Description}。外部后端仅提供只读快照；手动刷新后旧句柄失效。";
        // Page activation or navigation may already have read this selection while highlighting was pending.
        if (InspectionTabIndex != 9 || runtimeGeneration == _runtimeGeneration)
            await RefreshSelectionInspectionAsync();
    }

    private async Task LoadPropertiesAsync(TreeNodeViewModel node, CancellationToken cancellationToken, bool preserveDraft, string? committedProperty = null)
    {
        if (node.IsSynthetic) return;
        var loadSequence = ++_propertyLoadSequence;
        var includeClr = IsNativeBackend && IncludeClrProperties;
        var properties = IsNativeBackend
            ? await _client.GetPropertiesAsync(new PropertiesRequest(node.Id, includeClr), cancellationToken)
            : await _backend.GetPropertiesAsync(node.Id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (SelectedNode?.Id != node.Id || loadSequence < _appliedPropertyLoadSequence ||
            includeClr != (IsNativeBackend && IncludeClrProperties))
            return;
        _appliedPropertyLoadSequence = loadSequence;
        _propertyRowsObjectId = node.Id;
        var previous = _properties.ToDictionary(property => property.Id);
        _properties.Clear();
        foreach (var property in properties.OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase))
        {
            var displayed = IsNativeBackend ? property : property with { IsReadOnly = true, CanEdit = false, CanClear = false };
            if (previous.TryGetValue(property.Id, out var row))
                row.Refresh(displayed, preserveDraft && property.Id != committedProperty);
            else
                row = new PropertyRowViewModel(displayed, ApplyPropertyAsync, ClearPropertyAsync, ReportError, PreviewPropertyAsync);
            _properties.Add(row);
        }
        foreach (var removed in previous.Values.Where(row => !_properties.Contains(row))) removed.Dispose();
        UpdatePropertyCategories();
        FilterProperties();
    }

    private void FilterProperties()
    {
        var filter = PropertyFilter.Trim();
        var matching = _properties.Where(property =>
            (!IsNativeBackend || !ContextualProperties || property.IsContextRelevant) &&
            (PropertyCategory == "全部分类" || property.Category == PropertyCategory) &&
            (filter.Length == 0 || property.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            property.TypeName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            property.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            (property.CurrentValue?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
            property.DeclaringType.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        var desired = (PropertySortIndex switch
        {
            1 => matching.OrderByDescending(property => property.Name, StringComparer.OrdinalIgnoreCase),
            2 => matching.OrderBy(property => property.CurrentValue, StringComparer.OrdinalIgnoreCase).ThenBy(property => property.Name),
            3 => matching.OrderByDescending(property => property.CurrentValue, StringComparer.OrdinalIgnoreCase).ThenBy(property => property.Name),
            4 => matching.OrderBy(property => property.Category, StringComparer.OrdinalIgnoreCase).ThenBy(property => property.Name),
            _ => matching.OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
        }).ThenBy(property => property.Id, StringComparer.Ordinal).ToArray();
        string? previousCategory = null;
        foreach (var row in desired)
        {
            row.SetGroup(_propertyGroups.GetValueOrDefault(row.Category), PropertySortIndex == 4,
                PropertySortIndex == 4 && previousCategory != row.Category);
            previousCategory = row.Category;
        }
        var selected = SelectedProperty;
        for (var index = 0; index < desired.Length; index++)
        {
            if (index < FilteredProperties.Count && ReferenceEquals(FilteredProperties[index], desired[index]))
                continue;
            var existingIndex = FilteredProperties.IndexOf(desired[index]);
            if (existingIndex >= 0)
                FilteredProperties.Move(existingIndex, index);
            else
                FilteredProperties.Insert(index, desired[index]);
        }
        while (FilteredProperties.Count > desired.Length)
            FilteredProperties.RemoveAt(FilteredProperties.Count - 1);
        if (selected is not null && desired.Contains(selected)) SelectedProperty = selected;
        Raise(nameof(PropertyCountText));
    }

    private async Task ApplyPropertyAsync(PropertyRowViewModel property)
    {
        if (SelectedNode is not { IsPlaceholder: false, IsSynthetic: false } node || !CanUseNativeTools)
            return;
        var cancellationToken = _selectionCancellation?.Token ?? _lifetime.Token;
        if (property.BrushDraft is { } brush)
            await _client.SetBrushAsync(new SetBrushRequest(node.Id, property.Id, brush), cancellationToken);
        else
            await _client.SetPropertyAsync(node.Id, property.Id, property.Draft, cancellationToken);
        await LoadPropertiesAsync(node, cancellationToken, true, property.Id);
        Status = $"已临时修改 {property.Name}；清除或断开连接将恢复原来的值来源。";
    }

    private async Task ClearPropertyAsync(PropertyRowViewModel property)
    {
        if (SelectedNode is not { IsPlaceholder: false, IsSynthetic: false } node || !CanUseNativeTools)
            return;
        var cancellationToken = _selectionCancellation?.Token ?? _lifetime.Token;
        await _client.ClearPropertyAsync(node.Id, property.Id, cancellationToken);
        await LoadPropertiesAsync(node, cancellationToken, true, property.Id);
        Status = $"已清除 {property.Name} 的诊断修改。";
    }

    private async Task TogglePickingAsync()
    {
        if (!CanUseNativeTools) return;
        var desired = !_isPicking;
        await _client.SetPickingAsync(desired, _lifetime.Token);
        _isPicking = desired;
        Raise(nameof(PickButtonText));
        Status = desired ? "拾取已开启：请点击测试应用里的控件。再次点击取消拾取。" : "已取消拾取。";
    }

    private async Task LocatePickedAsync(long objectId, int pickGeneration)
    {
        if (!IsConnected || pickGeneration != _pickGeneration)
            return;
        _desiredSelectionId = objectId;
        Status = $"正在定位拾取对象 #{objectId}…";
        var cancellationToken = _treeCancellation?.Token ?? _lifetime.Token;
        var node = FindLoadedNode(objectId);
        if (node is null)
        {
            IReadOnlyList<TreeNodeInfo> path;
            try { path = await _backend.GetAncestorsAsync(objectId, CurrentTreeKind, cancellationToken); }
            catch (RpcException error) when (error.Code is "object_detached" or "object_expired")
            {
                if (pickGeneration != _pickGeneration) return;
                if (_desiredSelectionId == objectId)
                    _desiredSelectionId = SelectedNode is { IsPlaceholder: false } selected && selected.Id != objectId ? selected.Id : null;
                if (SelectedNode?.Id == objectId)
                {
                    CommitSelection(null);
                    await SelectNodeAsync(null);
                }
                Status = error.Code == "object_expired"
                    ? "该树节点已失效；模板可能已重建，或弱对象已释放。请刷新并重新展开所属控件。"
                    : CurrentTreeKind switch
                    {
                        TreeKind.Logical => "此对象不在当前逻辑树中，请切换到视觉树后重新拾取。",
                        TreeKind.Merged => "此对象不在当前混合树中；模板可能已重建。请刷新所属控件，或切换到视觉树后重试。",
                        _ => "该对象已从当前窗口树分离，请刷新后重试。"
                    };
                return;
            }
            if (pickGeneration != _pickGeneration || path.Count == 0) return;
            node = Roots.FirstOrDefault(root => root.Id == path[0].Id);
            if (node is null)
            {
                node = CreateNode(path[0], _treeGeneration);
                Roots.Add(node);
            }
            foreach (var step in path.Skip(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pickGeneration != _pickGeneration) return;
                var child = await EnsureChildLoadedAsync(node, step.Id, cancellationToken);
                if (child is null) { node = null; break; }
                node.IsExpanded = true;
                node = child;
            }
        }
        if (pickGeneration != _pickGeneration)
            return;
        if (node is null)
        {
            Status = CurrentTreeKind == TreeKind.Logical
                ? "此对象不在当前逻辑树中，请切换到视觉树后重新拾取。"
                : "未能沿目标对象的祖先路径定位，请刷新控件树后重试。";
            return;
        }
        var ancestors = new Stack<TreeNodeViewModel>();
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            ancestors.Push(parent);
        while (ancestors.TryPop(out var parent))
            parent.IsExpanded = true;
        CommitSelection(node);
        await SelectNodeAsync(node);
    }

    private void HandleConnectionChanged() => Dispatch(() =>
    {
        RefreshConnectionState();
        if (CanUseNativeTools) QueueOverlayConfiguration();
        if (!IsConnected && !_connecting)
        {
            ResetDisconnectedState();
            Status = "连接已结束。请确认测试应用仍在运行，再重新连接。";
        }
    });

    private void HandleSelectionChanged(SelectionChangedNotification notification) =>
        Dispatch(() =>
        {
            var generation = ++_pickGeneration;
            _ = RunSafelyAsync(() => LocatePickedAsync(notification.ObjectId, generation));
        });

    private void HandleTreeInvalidated(TreeInvalidatedNotification notification) => Dispatch(() =>
    {
        if (IsConnected && _backend.HasPushUpdates && !_treeRefreshTimer.IsEnabled)
            _treeRefreshTimer.Start();
    });

    private void HandlePropertiesChanged(PropertiesChangedNotification notification) => Dispatch(() =>
    {
        if (_backend.HasPushUpdates && SelectedNode?.Id == notification.ObjectId && !_propertyRefreshTimer.IsEnabled)
            _propertyRefreshTimer.Start();
    });

    private void HandlePickingChanged(PickingChangedNotification notification) => Dispatch(() =>
    {
        _isPicking = notification.IsPicking;
        Raise(nameof(PickButtonText));
    });

    private void HandleTreeRefreshTick(object? sender, EventArgs args)
    {
        _treeRefreshTimer.Stop();
        _ = RunSafelyAsync(() => RefreshTreeAsync(true));
    }

    private void HandlePropertyRefreshTick(object? sender, EventArgs args)
    {
        _propertyRefreshTimer.Stop();
        if (SelectedNode is { IsPlaceholder: false, IsSynthetic: false } node && IsConnected)
            _ = RunSafelyAsync(() => LoadPropertiesAsync(node, _selectionCancellation?.Token ?? _lifetime.Token, true));
    }

    private void Dispatch(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (!_disposed)
                action();
        }
        else
            Dispatcher.UIThread.Post(() => { if (!_disposed) action(); });
    }

    private async Task RunSafelyAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_disposed) ReportError(exception); }
    }

    private void ReportError(Exception exception)
    {
        Status = exception is RpcException rpc
            ? rpc.Code switch
            {
                "object_expired" => "该对象已释放，请刷新控件树后重新选择。",
                "not_connected" => "尚未连接应用，或连接已经结束。",
                "unsupported_version" => "目标应用与检查器的协议版本不一致，请使用同一版本构建。",
                _ => $"操作失败 [{rpc.Code}]：{rpc.Message}"
            }
            : $"操作失败：{exception.Message}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        ClearPropertyRows();
        DisposeInspection();
        CancelTreeExpansion();
        _lifetime.Cancel();
        _treeCancellation?.Cancel();
        _selectionCancellation?.Cancel();
        _treeRefreshTimer.Stop();
        _propertyRefreshTimer.Stop();
        _treeRefreshTimer.Tick -= HandleTreeRefreshTick;
        _propertyRefreshTimer.Tick -= HandlePropertyRefreshTick;
        _backend.ConnectionChanged -= HandleConnectionChanged;
        _client.SelectionChanged -= HandleSelectionChanged;
        _client.TreeInvalidated -= HandleTreeInvalidated;
        _client.PropertiesChanged -= HandlePropertiesChanged;
        _client.PickingChanged -= HandlePickingChanged;
        try
        {
            await _backend.DisposeAsync();
            if (!IsNativeBackend) await _client.DisposeAsync();
        }
        catch (Exception) { /* The window is closed; transport cleanup must not escape into UI callbacks. */ }
        _treeCancellation?.Dispose();
        _selectionCancellation?.Dispose();
        _lifetime.Dispose();
    }
}





