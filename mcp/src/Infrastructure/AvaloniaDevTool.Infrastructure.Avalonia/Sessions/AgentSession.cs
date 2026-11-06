using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Composes one target connection; feature state belongs to its capability services.</summary>
internal sealed class AgentSession : IDisposable
{
    private readonly WeakObjectRegistry _objects = new();
    private readonly RpcMethodRegistry _routes = new();
    private readonly SessionNotifications _notifications = new();
    private readonly ObjectObservationService _observations;
    private readonly TargetRootCatalog _roots;
    private readonly TreeInspectionService _tree;
    private readonly PropertyInspectionService _properties;
    private readonly StyleInspectionService _styles;
    private readonly SelectionInspectionService _selection;
    private readonly ResourceInspectionService _resources;
    private readonly ActivityService _activity;
    private readonly MetricsService _metrics;
    private readonly ProfilerService _profiler;
    private readonly BreakpointService _breakpoints;
    private readonly CompositionInspectionService _composition;
    private readonly InputInspectionService _input;
    private readonly AssetExportService _assetExports = new();
    private readonly OverlayService _overlay;
    private readonly RendererDiagnosticsService _renderer;
    private readonly PopupFreezeService _popups;
    private readonly DispatcherTimer _timer;
    private bool _disposed;

    public AgentSession(Application application, DiagnosticsAgentOptions options)
    {
        _observations = new ObjectObservationService(_objects);
        _roots = new TargetRootCatalog(application, options, _observations.Observe);
        var info = new SessionInfo(Guid.NewGuid().ToString("N"),
            options.ApplicationName,
            Environment.ProcessId,
            typeof(Application).Assembly.GetName().Version?.ToString() ?? "unknown",
            DiagnosticsProtocol.Version);
        _resources = new ResourceInspectionService(application, _objects);
        _activity = new ActivityService(_objects, _notifications.Publish, options.LogSources, _notifications.SendAsync);
        _metrics = new MetricsService(_notifications.Publish);
        _profiler = new ProfilerService(_notifications.Publish);
        _breakpoints = new BreakpointService(_objects, _notifications.Publish);
        var layout = new LayoutInspectionService(_objects, _observations.Observe);
        _composition = new CompositionInspectionService(_objects, _observations.Observe);
        var runtime = new RuntimeInspectionService(_objects);
        _input = new InputInspectionService(_objects);
        _overlay = new OverlayService(_objects, _observations.Observe);
        _renderer = new RendererDiagnosticsService(_objects);
        _popups = new PopupFreezeService(_objects);
        _tree = new TreeInspectionService(_objects,
            _observations,
            _roots.Refresh,
            _notifications.Publish,
            info.SessionId);
        _properties = new PropertyInspectionService(application, _objects, _observations, _notifications.Publish);
        _styles = new StyleInspectionService(_objects, _properties);
        _selection = new SelectionInspectionService(_objects,
            _overlay,
            _observations.Observe,
            _roots.Refresh,
            _notifications.Publish);
        var actions = new ElementActionService(_objects, _properties);
        var screenshots = new ElementScreenshotService(_objects,
            _overlay,
            id => _tree.GetAncestors(new TreeAncestorsRequest(id, TreeKind.Visual)));
        _roots.ConfiguredRootsRefreshed += _popups.RefreshRoots;
        _roots.PresentationRootsRefreshed += _selection.RefreshRoots;
        _roots.PresentationRootsRefreshed += _renderer.RefreshRoots;
        _roots.PresentationRootsRefreshed += _tree.RefreshRootObservations;
        _routes.Map(Methods.SessionInfo,
            () => info with { ContentGeneration = options.ContentGenerationProvider?.Invoke() ?? 0 });
        _routes.Map(PreviewMethods.ContentGeneration, () => options.ContentGenerationProvider?.Invoke() ?? 0);
        _tree.Register(_routes);
        _properties.Register(_routes);
        _styles.Register(_routes);
        _selection.Register(_routes);
        _routes.Map<ElementActionRequest>(ExplorationMethods.Action, actions.Perform);
        _routes.Map<CaptureElementRequest>(ExplorationMethods.Screenshot, screenshots.Capture);
        _routes.MapAsync<CompositionSnapshotRequest>(CompositionMethods.Snapshot,
            async (request, cancellation) => await _composition.CaptureAsync(request, cancellation));
        AssetsRpcRoutes.Register(_routes, _assetExports);
        InputRpcRoutes.Register(_routes, _input);
        LayoutRpcRoutes.Register(_routes, layout);
        OverlaysRpcRoutes.Register(_routes, _overlay);
        PopupsRpcRoutes.Register(_routes, _popups);
        RenderingRpcRoutes.Register(_routes, _renderer);
        ResourcesRpcRoutes.Register(_routes, _resources);
        RuntimeRpcRoutes.Register(_routes, runtime);
        ActivityRpcRoutes.Register(_routes, _activity);
        MetricsRpcRoutes.Register(_routes, _metrics);
        ProfilerRpcRoutes.Register(_routes, _profiler);
        BreakpointsRpcRoutes.Register(_routes, _breakpoints);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += OnTick;
    }

    public void Connect(RpcPeer peer)
    {
        _notifications.Connect(peer);
        _tree.InitializeRoots();
        _timer.Start();
    }

    public object Handle(string method, JsonElement parameters)
    {
        VerifyAccess();
        return _routes.Invoke(method, parameters);
    }

    public async Task<object?> HandleAsync(string method, JsonElement parameters, CancellationToken cancellation)
    {
        VerifyAccess();
        return await _routes.InvokeAsync(method, parameters, cancellation);
    }

    private void VerifyAccess()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal void NotifyContentChanged(long generation) =>
        _notifications.Publish(PreviewMethods.ContentGenerationChanged, generation);

    private void OnTick(object? sender, EventArgs args)
    {
        if (_disposed)
            return;
        _tree.PollRoots();
        _properties.FlushChanges();
        _observations.Prune();
        _objects.Prune();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _notifications.Dispose();
        _selection.Dispose();
        _input.Dispose();
        _overlay.Dispose();
        _renderer.Dispose();
        _popups.Dispose();
        _assetExports.Dispose();
        _composition.Dispose();
        _styles.Dispose();
        _activity.Dispose();
        _metrics.Dispose();
        _profiler.Dispose();
        _breakpoints.Dispose();
        _resources.Dispose();
        _properties.Dispose();
        _tree.Dispose();
        _observations.Dispose();
    }
}
