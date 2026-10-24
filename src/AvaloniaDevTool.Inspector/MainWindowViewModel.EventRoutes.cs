using System.Collections.ObjectModel;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private EventRouteInfo? _selectedEventRoute;
    private EventRouteNode? _selectedEventRouteNode;
    private EventRouteStatus? _eventRouteStatus;
    private string _eventCatalogFilter = "";
    private int _eventRoutesGeneration;
    private bool _eventDefaultsLoaded;
    private EventSelection? _savedEventDefaults;
    private string _eventDefaultsSummary = "默认启用六组；可保存当前选择供下次连接使用。";
    private string _missingEventDefaultsSummary = "";
    public EventSelectionDefaultsStore EventDefaultsStore { get; set; } = new(EventSelectionDefaultsStore.DefaultPath);
    public string EventDefaultsSummary { get => _eventDefaultsSummary; private set => Set(ref _eventDefaultsSummary, value); }
    public string MissingEventDefaultsSummary { get => _missingEventDefaultsSummary; private set => Set(ref _missingEventDefaultsSummary, value); }
    public ObservableCollection<EventCatalogChoice> EventCatalogChoices { get; } = [];
    public ObservableCollection<EventGroupChoice> EventGroupChoices { get; } = [];
    public ObservableCollection<EventRouteInfo> EventRoutes { get; } = [];
    public IEnumerable<EventCatalogChoice> FilteredEventCatalog => EventCatalogChoices.Where(item =>
        EventCatalogFilter.Length == 0 || item.Description.Contains(EventCatalogFilter, StringComparison.OrdinalIgnoreCase));
    public string EventCatalogFilter { get => _eventCatalogFilter; set { if (Set(ref _eventCatalogFilter, value ?? "")) Raise(nameof(FilteredEventCatalog)); } }
    public EventRouteInfo? SelectedEventRoute
    {
        get => _selectedEventRoute;
        set
        {
            if (!Set(ref _selectedEventRoute, value)) return;
            SelectedEventRouteNode = null;
            Raise(nameof(SelectedEventRouteNodes));
            Raise(nameof(SelectedEventRouteSummary));
            RefreshEventRouteCommands();
        }
    }
    public EventRouteNode? SelectedEventRouteNode
    {
        get => _selectedEventRouteNode;
        set { if (Set(ref _selectedEventRouteNode, value)) RefreshEventRouteCommands(); }
    }
    public IReadOnlyList<EventRouteNode> SelectedEventRouteNodes => SelectedEventRoute?.Nodes ?? [];
    public string SelectedEventRouteSummary => SelectedEventRoute is { } route
        ? $"#{route.RouteId} · {route.OwnerType}.{route.EventName} · 源 #{route.SourceObjectId} {route.SourceType} {route.SourceName}" +
          (route.SourceExpired ? "（源已失效）" : "") +
          $"\n{route.State} · 已完成策略 {string.Join(", ", route.FinishedStrategies)} · 最终 Handled={route.FinalHandled}" +
          (route.PhaseFinishes is { Count: > 0 } phases ? "\n" + string.Join(" · ", phases.Select(phase => $"{phase.Strategy}完成 Handled={phase.HandledObserved}" + (phase.HandledChangedSincePreviousObservation ? "（相邻观察间变化）" : ""))) : "") +
          (route.IsTruncated ? " · 路由已截断" : "") +
          "\nHandled 为 Raised 类处理器观察点的值；变化发生在相邻观察之间，不能归因到具体实例处理器。"
        : "选择一次事件查看真实 Tunnel / Bubble / Direct 观察节点。";
    public string EventRoutesSummary => _eventRouteStatus is { } status
        ? $"{EventRoutes.Count}/{status.Capacity} 次事件 · {status.BufferedBytes:N0}/{status.ByteBudget:N0} 字节 · 淘汰 {status.DroppedCount} · 截断 {status.TruncatedCount} · 活动 TTL {status.ActiveRouteTtlSeconds}s / 历史 TTL {status.HistoryTtlSeconds}s"
        : "刷新已注册事件目录，然后按分组或单个事件开始捕获。";
    public AsyncCommand RefreshEventRoutesCommand { get; private set; } = null!;
    public AsyncCommand StartEventRoutesCommand { get; private set; } = null!;
    public AsyncCommand ClearEventRoutesCommand { get; private set; } = null!;
    public AsyncCommand DefaultEventGroupsCommand { get; private set; } = null!;
    public AsyncCommand SaveEventDefaultsCommand { get; private set; } = null!;
    public AsyncCommand JumpToEventSourceCommand { get; private set; } = null!;
    public AsyncCommand JumpToEventNodeCommand { get; private set; } = null!;

    private void InitializeEventRoutes()
    {
        RefreshEventRoutesCommand = new(RefreshEventRoutesAsync, ReportError, () => CanUseNativeTools);
        StartEventRoutesCommand = new(StartEventRoutesAsync, ReportError, () => CanUseNativeTools && _activityStatus?.IsRunning != true);
        ClearEventRoutesCommand = new(async () => { await ClearActivityAsync(); ResetEventRouteRows(); }, ReportError, () => CanUseNativeTools);
        DefaultEventGroupsCommand = new(() =>
        {
            foreach (var group in EventGroupChoices) group.IsSelected = true;
            foreach (var item in EventCatalogChoices) item.IsSelected = false;
            return Task.CompletedTask;
        }, ReportError, () => CanUseNativeTools);
        SaveEventDefaultsCommand = new(async () =>
        {
            var selection = CurrentEventSelection();
            await EventDefaultsStore.SaveAsync(selection, _lifetime.Token);
            _savedEventDefaults = selection;
            _eventDefaultsLoaded = true;
            EventDefaultsSummary = "已保存当前事件与分组；下次连接使用此默认集。";
            MissingEventDefaultsSummary = "";
        }, ReportError, () => CanUseNativeTools);
        JumpToEventSourceCommand = new(async () =>
        {
            if (SelectedEventRoute?.SourceObjectId is { } id) await LocatePickedAsync(id, ++_pickGeneration);
        }, ReportError, () => CanUseNativeTools && SelectedEventRoute?.SourceObjectId is not null);
        JumpToEventNodeCommand = new(async () =>
        {
            if (SelectedEventRouteNode?.ReceiverObjectId is { } id) await LocatePickedAsync(id, ++_pickGeneration);
        }, ReportError, () => CanUseNativeTools && SelectedEventRouteNode?.ReceiverObjectId is not null);
        _client.EventRouteUpdated += HandleEventRouteUpdated;
        _client.EventRouteStateChanged += HandleEventRouteStateChanged;
    }

    private void RefreshEventRouteCommands()
    {
        RefreshEventRoutesCommand?.Refresh();
        StartEventRoutesCommand?.Refresh();
        ClearEventRoutesCommand?.Refresh();
        DefaultEventGroupsCommand?.Refresh();
        SaveEventDefaultsCommand?.Refresh();
        JumpToEventSourceCommand?.Refresh();
        JumpToEventNodeCommand?.Refresh();
    }

    public async Task RefreshEventRoutesAsync()
    {
        if (!CanUseNativeTools) return;
        var generation = ++_eventRoutesGeneration;
        var session = _backend.Session?.SessionId;
        if (!_eventDefaultsLoaded)
        {
            try
            {
                _savedEventDefaults = await EventDefaultsStore.LoadAsync(_lifetime.Token);
                if (_savedEventDefaults is not null) EventDefaultsSummary = "已载入保存的默认事件集；当前会话可覆盖选择。";
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { EventDefaultsSummary = "默认配置不可读，已使用默认六组。"; }
            _eventDefaultsLoaded = true;
        }
        var catalog = await _client.GetEventCatalogAsync(_lifetime.Token);
        var routes = await _client.GetEventRoutesAsync(_lifetime.Token);
        var status = await _client.GetEventRouteStatusAsync(_lifetime.Token);
        if (generation != _eventRoutesGeneration || !CanUseNativeTools || session != _backend.Session?.SessionId || _disposed) return;
        var registered = catalog.Events.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var missingDefaults = _savedEventDefaults?.EventIds?.Where(id => !registered.Contains(id)).ToArray() ?? [];
        MissingEventDefaultsSummary = missingDefaults.Length == 0 ? "" :
            $"已保存默认中有 {missingDefaults.Length} 个事件在此目标尚未注册，未应用本次监听：" +
            string.Join(", ", missingDefaults.Take(4)) + (missingDefaults.Length > 4 ? "…" : "") + "。注册后刷新并重新开始捕获。";
        for (var index = EventCatalogChoices.Count - 1; index >= 0; index--)
            if (!registered.Contains(EventCatalogChoices[index].Entry.Id)) EventCatalogChoices.RemoveAt(index);
        var existingIds = EventCatalogChoices.Select(item => item.Entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in catalog.Events)
            if (existingIds.Add(item.Id)) EventCatalogChoices.Add(new(item) { IsSelected = _savedEventDefaults?.EventIds?.Contains(item.Id, StringComparer.Ordinal) == true });
        if (EventGroupChoices.Count == 0)
            Replace(EventGroupChoices, catalog.DefaultGroups.Select(item => new EventGroupChoice(item)
            { IsSelected = _savedEventDefaults is null || _savedEventDefaults.IncludeAll || _savedEventDefaults.Groups?.Contains(item, StringComparer.Ordinal) == true }));
        Raise(nameof(FilteredEventCatalog));
        var ids = routes.Select(item => item.RouteId).ToHashSet();
        for (var index = EventRoutes.Count - 1; index >= 0; index--)
            if (!ids.Contains(EventRoutes[index].RouteId)) EventRoutes.RemoveAt(index);
        foreach (var route in routes) ApplyEventRoute(route);
        if (SelectedEventRoute is { } current && !ids.Contains(current.RouteId)) SelectedEventRoute = null;
        _eventRouteStatus = status;
        Raise(nameof(EventRoutesSummary));
        RefreshEventRouteCommands();
    }

    private async Task StartEventRoutesAsync()
    {
        if (ActivitySelectedSourceOnly && !HasSelectedNativeElement())
            throw new RpcException("select_source", "请先选择要监听事件源的控件。");
        if (EventCatalogChoices.Count == 0) await RefreshEventRoutesAsync();
        var selection = CurrentEventSelection();
        if (selection.EventIds!.Count == 0 && selection.Groups!.Count == 0)
            throw new RpcException("select_event", "请选择至少一个事件或分组。");
        CaptureEvents = true;
        await _client.StartActivityAsync(new(CaptureLogs, true, ActivitySelectedSourceOnly ? SelectedNode?.Id : null,
            MinimumLogLevel: MinimumLogLevel, LogFilter: CurrentLogFilter(), EventSelection: selection), _lifetime.Token);
        SetActivityStatus(await _client.GetActivityStatusAsync(_lifetime.Token));
        await RefreshEventRoutesAsync();
        Status = "按已选注册事件与分组开始捕获；分组会包含后续注册的事件。";
    }

    private EventSelection CurrentEventSelection() => new(EventCatalogChoices.Where(item => item.IsSelected).Select(item => item.Entry.Id).ToArray(),
        EventGroupChoices.Where(item => item.IsSelected).Select(item => item.Id).ToArray());

    private void HandleEventRouteUpdated(EventRouteInfo route)
    {
        var session = _backend.Session?.SessionId;
        var generation = _eventRoutesGeneration;
        Dispatch(() =>
        {
            if (!CanUseNativeTools || session != _backend.Session?.SessionId || generation != _eventRoutesGeneration || _disposed) return;
            ApplyEventRoute(route);
        });
    }

    private void ApplyEventRoute(EventRouteInfo route)
    {
        var wasSelected = SelectedEventRoute?.RouteId == route.RouteId;
        var selectedNodeIndex = SelectedEventRouteNode?.Index;
        var existing = EventRoutes.FirstOrDefault(item => item.RouteId == route.RouteId);
        if (existing is not null && (existing.Nodes.Count > route.Nodes.Count || existing.IsFinished && !route.IsFinished ||
            existing.State != "Active" && route.State == "Active")) return;
        if (existing?.SourceExpired == true && !route.SourceExpired) route = route with { SourceExpired = true };
        if (existing is not null) EventRoutes[EventRoutes.IndexOf(existing)] = route;
        else
        {
            while (EventRoutes.Count >= 200) EventRoutes.RemoveAt(0);
            EventRoutes.Add(route);
        }
        if (wasSelected)
        {
            _selectedEventRoute = route;
            Raise(nameof(SelectedEventRoute));
            Raise(nameof(SelectedEventRouteNodes));
            Raise(nameof(SelectedEventRouteSummary));
            SelectedEventRouteNode = selectedNodeIndex is { } selected ? route.Nodes.FirstOrDefault(item => item.Index == selected) : null;
        }
        Raise(nameof(EventRoutesSummary));
    }

    private void HandleEventRouteStateChanged(EventRouteStatus state)
    {
        var session = _backend.Session?.SessionId;
        var generation = _eventRoutesGeneration;
        Dispatch(() =>
        {
            if (!CanUseNativeTools || session != _backend.Session?.SessionId || generation != _eventRoutesGeneration || _disposed) return;
            _eventRouteStatus = state;
            if (state.BufferedCount == 0)
            {
                EventRoutes.Clear();
                SelectedEventRoute = null;
            }
            Raise(nameof(EventRoutesSummary));
        });
    }

    private void ResetEventRouteRows()
    {
        _eventRoutesGeneration++;
        EventRoutes.Clear();
        SelectedEventRoute = null;
        _eventRouteStatus = null;
        Raise(nameof(EventRoutesSummary));
    }

    private void ResetEventRoutes()
    {
        ResetEventRouteRows();
        EventCatalogChoices.Clear();
        EventGroupChoices.Clear();
        MissingEventDefaultsSummary = "";
        Raise(nameof(FilteredEventCatalog));
        RefreshEventRouteCommands();
    }

    private void DisposeEventRoutes()
    {
        _eventRoutesGeneration++;
        _client.EventRouteUpdated -= HandleEventRouteUpdated;
        _client.EventRouteStateChanged -= HandleEventRouteStateChanged;
    }
}

public sealed class EventCatalogChoice(EventCatalogEntry entry) : ObservableObject
{
    private bool _isSelected;
    public EventCatalogEntry Entry { get; } = entry;
    public string Description => $"{Entry.OwnerType}.{Entry.Name} · {Entry.RoutingStrategies} · {Entry.Group}";
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

public sealed class EventGroupChoice(string id) : ObservableObject
{
    private bool _isSelected;
    public string Id { get; } = id;
    public string Label => Id switch { "Pointer" => "指针", "Keyboard" => "键盘与文本", "Focus" => "焦点", "Control" => "控件", "Gesture" => "手势", "DragDrop" => "拖放", _ => Id };
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}
