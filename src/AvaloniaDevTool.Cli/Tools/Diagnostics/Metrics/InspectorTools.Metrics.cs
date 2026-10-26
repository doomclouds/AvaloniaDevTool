using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "metrics-start"),
     Description(
         "Start bounded .NET Meter capture. Filters accept globs; defaults capture Avalonia* and System.Runtime. Raw numeric strings preserve integer/decimal precision.")]
    public async Task<MetricsStatusDto> StartMetrics(string[]? meterNames = null,
        string[]? instrumentNames = null,
        int capacity = 512,
        int pollIntervalMs = 1000,
        int notificationIntervalMs = 200,
        int historyWindowMs = 60000,
        int frameIntervalMs = 250,
        bool aggregateFrames = true,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).StartMetricsAsync(new MetricsStartRequest(
                MeterNames: meterNames,
                InstrumentNames: instrumentNames,
                Capacity: capacity,
                PollIntervalMs: pollIntervalMs,
                NotificationIntervalMs: notificationIntervalMs,
                HistoryWindowMs: historyWindowMs,
                FrameIntervalMs: frameIntervalMs,
                AggregateFrames: aggregateFrames),
            cancellationToken);

    [McpServerTool(Name = "metrics-clear"),
     Description(
         "Clear one metric series or all retained samples and pending measurements while keeping the capture running. A new revision prevents stale snapshots from restoring cleared samples.")]
    public async Task<MetricsStatusDto> ClearMetrics(long? seriesId = null,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).ClearMetricsAsync(new MetricsClearRequest(seriesId),
            cancellationToken);

    [McpServerTool(Name = "metrics-stop"),
     Description("Stop metric listeners and observable polling while retaining the bounded captured window.")]
    public async Task<MetricsStatusDto> StopMetrics(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).StopMetricsAsync(cancellationToken);

    [McpServerTool(Name = "metrics", ReadOnly = true),
     Description(
         "Read captured instruments, tagged samples and per-series retained-window summaries. Percentiles describe retained samples, not the complete measurement history.")]
    public async Task<MetricsSnapshotDto> Metrics(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).GetMetricsSnapshotAsync(cancellationToken);
}
