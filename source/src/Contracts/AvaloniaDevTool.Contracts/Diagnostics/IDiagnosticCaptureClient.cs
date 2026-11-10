using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

/// <summary>The existing connection's diagnostic operations required by capture orchestration.</summary>
public interface IDiagnosticCaptureClient
{
    bool IsConnected { get; }
    SessionInfo? Session { get; }
    long GetDiagnosticMutationEpoch(DiagnosticCaptureService service);

    Task<T> RunOwnedDiagnosticMutationAsync<T>(DiagnosticCaptureService service,
        long expectedEpoch,
        Func<Task<T>> mutation);

    Task<MetricsStatusDto> StartMetricsAsync(MetricsStartRequest request,
        CancellationToken cancellationToken = default);

    Task<MetricsStatusDto> StopMetricsAsync(CancellationToken cancellationToken = default);
    Task<MetricsStatusDto> GetMetricsStatusAsync(CancellationToken cancellationToken = default);
    Task<MetricsSnapshotDto> GetMetricsSnapshotAsync(CancellationToken cancellationToken = default);

    Task<ProfilerStatusDto> StartProfilerAsync(ProfilerStartRequest request,
        CancellationToken cancellationToken = default);

    Task<ProfilerStatusDto> StopProfilerAsync(CancellationToken cancellationToken = default);
    Task<ProfilerStatusDto> GetProfilerStatusAsync(CancellationToken cancellationToken = default);
    Task<ProfilerSnapshotDto> GetProfilerSnapshotAsync(CancellationToken cancellationToken = default);
    Task StartActivityAsync(ActivityStartRequest request, CancellationToken cancellationToken = default);
    Task StopActivityAsync(CancellationToken cancellationToken = default);
    Task<ActivityStatus> GetActivityStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ActivityEntry>> GetActivityAsync(CancellationToken cancellationToken = default);
}
