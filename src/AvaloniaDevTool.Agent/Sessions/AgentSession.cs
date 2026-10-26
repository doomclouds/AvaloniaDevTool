using System.Text.Json;
using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession : IDisposable
{
    private readonly Application _application;
    private readonly DiagnosticsAgentOptions _options;
    private readonly WeakObjectRegistry _objects = new();
    private readonly SessionInfo _info;
    private readonly ResourceInspectionService _resources;
    private readonly ActivityService _activity;
    private readonly MetricsService _metrics;
    private readonly ProfilerService _profiler;
    private readonly BreakpointService _breakpoints;
    private readonly LayoutInspectionService _layout;
    private readonly CompositionInspectionService _composition;
    private readonly RuntimeInspectionService _runtime;
    private readonly InputInspectionService _input;
    private readonly AssetExportService _assetExports = new();
    private readonly OverlayService _overlay;
    private readonly RendererDiagnosticsService _rendererDiagnostics;
    private readonly PopupFreezeService _popupFreeze;
    private bool _disposed;

    public AgentSession(Application application, DiagnosticsAgentOptions options)
    {
        _application = application;
        _options = options;
        _resources = new ResourceInspectionService(application, _objects);
        _activity = new ActivityService(_objects, Notify, options.LogSources, NotifyActivityAsync);
        _metrics = new MetricsService(Notify);
        _profiler = new ProfilerService(Notify);
        _breakpoints = new BreakpointService(_objects, Notify);
        _layout = new LayoutInspectionService(_objects, Observe);
        _composition = new CompositionInspectionService(_objects, Observe);
        _runtime = new RuntimeInspectionService(_objects);
        _input = new InputInspectionService(_objects);
        _overlay = new OverlayService(_objects, Observe);
        _rendererDiagnostics = new RendererDiagnosticsService(_objects);
        _popupFreeze = new PopupFreezeService(_objects);
        _info = new SessionInfo(Guid.NewGuid().ToString("N"),
            options.ApplicationName,
            Environment.ProcessId,
            typeof(Application).Assembly.GetName().Version?.ToString() ?? "unknown",
            DiagnosticsProtocol.Version);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += OnTick;
        RegisterSyncMethods();
    }

    public void Connect(RpcPeer peer)
    {
        _peer = peer;
        _rootsSnapshot = Fingerprint(RefreshRoots());
        _timer.Start();
    }

    public object Handle(string method, JsonElement parameters)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return method is not null && _syncMethods.TryGetValue(method, out var handler)
            ? handler(parameters)
            : HandleExploration(method!, parameters);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _input.Dispose();
        _overlay.Dispose();
        _rendererDiagnostics.Dispose();
        _popupFreeze.Dispose();
        _assetExports.Dispose();
        _composition.Dispose();
        DisposeExploration();
        _activity.Dispose();
        _metrics.Dispose();
        _profiler.Dispose();
        _breakpoints.Dispose();
        _resources.Dispose();
        _timer.Stop();
        _timer.Tick -= OnTick;
        _picking = false;
        ClearHighlight();
        foreach (var subscription in _rootHandlers.Values)
            subscription.Dispose();
        _rootHandlers.Clear();
        foreach (var reference in _observed.Values)
        {
            if (reference.TryGetTarget(out var target))
            {
                target.PropertyChanged -= OnPropertyChanged;
                if (target is TemplatedControl owner)
                    owner.TemplateApplied -= OnMergedTemplateApplied;
                if (_overrides.TryGetValue(target, out var entries))
                {
                    foreach (var entry in entries.Values)
                        entry.Dispose();
                    entries.Clear();
                }
            }
        }

        _overrides.Clear();
        _observed.Clear();
        _propertyContextParents.Clear();
        DisposeTreeObservations();
        _pendingProperties.Clear();
        _peer = null;
    }
}
