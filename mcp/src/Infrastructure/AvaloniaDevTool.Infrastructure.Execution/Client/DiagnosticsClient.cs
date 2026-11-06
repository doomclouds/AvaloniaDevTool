using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

/// <summary>Owns one diagnostics connection and exposes its capability clients.</summary>
public sealed class DiagnosticsClient : IDiagnosticCaptureClient, IAsyncDisposable
{
    private readonly DiagnosticsRpcContext context;

    public SelectionClient Selection { get; }

    public TreeClient Tree { get; }

    public PropertiesClient Properties { get; }

    public AssetsClient Assets { get; }

    public InputClient Input { get; }

    public LayoutClient Layout { get; }

    public OverlaysClient Overlays { get; }

    public PopupsClient Popups { get; }

    public PreviewClient Preview { get; }

    public RenderingClient Rendering { get; }

    public ResourcesClient Resources { get; }

    public RuntimeClient Runtime { get; }

    public StylesClient Styles { get; }

    public ActivityClient Activity { get; }

    public BreakpointsClient Breakpoints { get; }

    public MetricsClient Metrics { get; }

    public ProfilerClient Profiler { get; }

    public DiagnosticsClient()
    {
        context = new DiagnosticsRpcContext();
        Selection = new SelectionClient(context);
        Tree = new TreeClient(context);
        Properties = new PropertiesClient(context);
        Assets = new AssetsClient(context);
        Input = new InputClient(context);
        Layout = new LayoutClient(context);
        Overlays = new OverlaysClient(context);
        Popups = new PopupsClient(context);
        Preview = new PreviewClient(context.Connection,
            cancellation => context.InvokeAsync<long>(PreviewMethods.ContentGeneration, null, cancellation));
        Rendering = new RenderingClient(context);
        Resources = new ResourcesClient(context);
        Runtime = new RuntimeClient(context);
        Styles = new StylesClient(context);
        Activity = new ActivityClient(context);
        Breakpoints = new BreakpointsClient(context);
        Metrics = new MetricsClient(context);
        Profiler = new ProfilerClient(context);
    }

    public bool IsConnected => context.Connection.IsConnected;

    public SessionInfo? Session => context.Connection.Session;

    public RpcTraceBuffer ProtocolTrace => context.Connection.ProtocolTrace;

    public event Action? ConnectionChanged
    {
        add => context.Connection.ConnectionChanged += value;
        remove => context.Connection.ConnectionChanged -= value;
    }

    Task IDiagnosticCaptureClient.
        StartActivityAsync(ActivityStartRequest request, CancellationToken cancellationToken) =>
        Activity.StartActivityAsync(request, cancellationToken);

    Task IDiagnosticCaptureClient.StopActivityAsync(CancellationToken cancellationToken) =>
        Activity.StopActivityAsync(cancellationToken);

    Task<IReadOnlyList<ActivityEntry>> IDiagnosticCaptureClient.GetActivityAsync(CancellationToken cancellationToken) =>
        Activity.GetActivityAsync(cancellationToken);

    Task<ActivityStatus> IDiagnosticCaptureClient.GetActivityStatusAsync(CancellationToken cancellationToken) =>
        Activity.GetActivityStatusAsync(cancellationToken);

    Task<MetricsStatusDto> IDiagnosticCaptureClient.StartMetricsAsync(MetricsStartRequest request,
        CancellationToken cancellationToken) => Metrics.StartMetricsAsync(request, cancellationToken);

    Task<MetricsStatusDto> IDiagnosticCaptureClient.StopMetricsAsync(CancellationToken cancellationToken) =>
        Metrics.StopMetricsAsync(cancellationToken);

    Task<MetricsSnapshotDto> IDiagnosticCaptureClient.GetMetricsSnapshotAsync(CancellationToken cancellationToken) =>
        Metrics.GetMetricsSnapshotAsync(cancellationToken);

    Task<MetricsStatusDto> IDiagnosticCaptureClient.GetMetricsStatusAsync(CancellationToken cancellationToken) =>
        Metrics.GetMetricsStatusAsync(cancellationToken);

    Task<ProfilerStatusDto> IDiagnosticCaptureClient.StartProfilerAsync(ProfilerStartRequest request,
        CancellationToken cancellationToken) => Profiler.StartProfilerAsync(request, cancellationToken);

    Task<ProfilerStatusDto> IDiagnosticCaptureClient.StopProfilerAsync(CancellationToken cancellationToken) =>
        Profiler.StopProfilerAsync(cancellationToken);

    Task<ProfilerStatusDto> IDiagnosticCaptureClient.GetProfilerStatusAsync(CancellationToken cancellationToken) =>
        Profiler.GetProfilerStatusAsync(cancellationToken);

    Task<ProfilerSnapshotDto> IDiagnosticCaptureClient.GetProfilerSnapshotAsync(CancellationToken cancellationToken) =>
        Profiler.GetProfilerSnapshotAsync(cancellationToken);

    public Task ConnectAsync(string pipeName, CancellationToken cancellationToken = default) =>
        context.Connection.ConnectAsync(pipeName, cancellationToken);

    public Task DisconnectAsync() => context.Connection.DisconnectAsync();

    public ValueTask DisposeAsync() => context.Connection.DisposeAsync();

    /// <summary>Connects to a native RPC WebSocket endpoint using an Authorization header.</summary>
    /// <remarks>Plaintext is restricted to literal loopback addresses; normal TLS validation is retained.</remarks>
    public Task ConnectRemoteAsync(Uri endpoint, string bearerToken, CancellationToken cancellationToken = default) =>
        context.Connection.ConnectRemoteAsync(endpoint, bearerToken, cancellationToken);

    long IDiagnosticCaptureClient.GetDiagnosticMutationEpoch(DiagnosticCaptureService service) =>
        context.GetDiagnosticMutationEpoch(service);

    Task<T> IDiagnosticCaptureClient.RunOwnedDiagnosticMutationAsync<T>(DiagnosticCaptureService service,
        long expectedEpoch,
        Func<Task<T>> mutation) => context.RunOwnedDiagnosticMutationAsync(service, expectedEpoch, mutation);
}
