using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "profiler-start"),
     Description(
         "Capture actual Avalonia style matching, style activator evaluation and resource lookup activities. Durations are microseconds; filters match selector or resource key text. Aggregates describe the retained sample window.")]
    public async Task<ProfilerStatusDto> StartProfiler(string[]? categories = null,
        string? filter = null,
        int capacity = 512,
        int pendingCapacity = 256,
        int maxGroups = 128,
        CancellationToken cancellationToken = default)
    {
        var parsed = categories?.Select(value =>
            Enum.TryParse<ProfilerCategory>(value, true, out var category) && Enum.IsDefined(category)
                ? category
                : throw new McpException(
                    "invalid_request: Categories must be StyleAttach, StyleEvaluate or ResourceLookup.")).ToArray();
        return await (await session.GetAsync(cancellationToken)).StartProfilerAsync(
            new ProfilerStartRequest(parsed, filter, capacity, pendingCapacity, maxGroups),
            cancellationToken);
    }

    [McpServerTool(Name = "profiler-stop"),
     Description(
         "Stop the current profiler listener while retaining captured samples. Activities still in flight are counted as incomplete.")]
    public async Task<ProfilerStatusDto> StopProfiler(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).StopProfilerAsync(cancellationToken);

    [McpServerTool(Name = "profiler-clear"),
     Description("Clear profiler samples and counters without changing the selected capture categories.")]
    public async Task<ProfilerStatusDto> ClearProfiler(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).ClearProfilerAsync(cancellationToken);

    [McpServerTool(Name = "profiler", ReadOnly = true),
     Description(
         "Read bounded real Avalonia activity samples, retained-window aggregates, capture limits and loss counters. Missing resource Result tags remain unconfirmed outcomes.")]
    public async Task<ProfilerSnapshotDto> Profiler(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).GetProfilerSnapshotAsync(cancellationToken);
}
