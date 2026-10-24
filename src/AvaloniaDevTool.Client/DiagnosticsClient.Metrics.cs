using System.Text.Json;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public event Action<MetricsSamplesAddedNotification>? MetricsSamplesAdded;

    public Task<MetricsStatusDto> StartMetricsAsync(MetricsStartRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<MetricsStatusDto>(MetricsMethods.Start, request, cancellationToken);

    public Task<MetricsStatusDto> StopMetricsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<MetricsStatusDto>(MetricsMethods.Stop, null, cancellationToken);

    public Task<MetricsSnapshotDto> GetMetricsSnapshotAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<MetricsSnapshotDto>(MetricsMethods.Snapshot, null, cancellationToken);

    public Task<MetricsStatusDto> GetMetricsStatusAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<MetricsStatusDto>(MetricsMethods.Status, null, cancellationToken);

    public Task<MetricsStatusDto> ClearMetricsAsync(MetricsClearRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<MetricsStatusDto>(MetricsMethods.Clear, request, cancellationToken);

    private void HandleMetricsSamplesAdded(JsonElement payload) =>
        MetricsSamplesAdded?.Invoke(ProtocolJson.Read<MetricsSamplesAddedNotification>(payload));
}

