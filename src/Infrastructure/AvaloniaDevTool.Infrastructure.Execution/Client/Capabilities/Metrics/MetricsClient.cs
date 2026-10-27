using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class MetricsClient
{
    private readonly DiagnosticsRpcContext context;

    internal MetricsClient(DiagnosticsRpcContext context)
    {
        this.context = context;
        context.Subscribe((method, payload) =>
        {
            switch (method)
            {
                case MetricsMethods.SampleAdded:
                    MetricsSamplesAdded?.Invoke(ProtocolJson.Read<MetricsSamplesAddedNotification>(payload));
                    return true;
                default:
                    return false;
            }
        });
    }

    public event Action<MetricsSamplesAddedNotification>? MetricsSamplesAdded;

    public Task<MetricsStatusDto> StartMetricsAsync(MetricsStartRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<MetricsStatusDto>(MetricsMethods.Start, request, cancellationToken);

    public Task<MetricsStatusDto> StopMetricsAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<MetricsStatusDto>(MetricsMethods.Stop, null, cancellationToken);

    public Task<MetricsSnapshotDto> GetMetricsSnapshotAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<MetricsSnapshotDto>(MetricsMethods.Snapshot, null, cancellationToken);

    public Task<MetricsStatusDto> GetMetricsStatusAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<MetricsStatusDto>(MetricsMethods.Status, null, cancellationToken);

    public Task<MetricsStatusDto> ClearMetricsAsync(MetricsClearRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<MetricsStatusDto>(MetricsMethods.Clear, request, cancellationToken);
}
