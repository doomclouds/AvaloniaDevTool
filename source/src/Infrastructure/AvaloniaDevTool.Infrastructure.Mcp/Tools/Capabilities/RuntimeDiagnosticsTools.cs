using AvaloniaDevTool.Client;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace AvaloniaDevTool.Cli;

[McpServerToolType]
public sealed class RuntimeDiagnosticsTools(ToolSession session) : TargetToolBase(session)
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
        await (await session.GetAsync(cancellationToken)).Metrics.StartMetricsAsync(new MetricsStartRequest(
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
        await (await session.GetAsync(cancellationToken)).Metrics.ClearMetricsAsync(new MetricsClearRequest(seriesId),
            cancellationToken);

    [McpServerTool(Name = "metrics-stop"),
     Description("Stop metric listeners and observable polling while retaining the bounded captured window.")]
    public async Task<MetricsStatusDto> StopMetrics(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).Metrics.StopMetricsAsync(cancellationToken);

    [McpServerTool(Name = "metrics", ReadOnly = true),
     Description(
         "Read captured instruments, tagged samples and per-series retained-window summaries. Percentiles describe retained samples, not the complete measurement history.")]
    public async Task<MetricsSnapshotDto> Metrics(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).Metrics.GetMetricsSnapshotAsync(cancellationToken);

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
        return await (await session.GetAsync(cancellationToken)).Profiler.StartProfilerAsync(
            new ProfilerStartRequest(parsed, filter, capacity, pendingCapacity, maxGroups),
            cancellationToken);
    }

    [McpServerTool(Name = "profiler-stop"),
     Description(
         "Stop the current profiler listener while retaining captured samples. Activities still in flight are counted as incomplete.")]
    public async Task<ProfilerStatusDto> StopProfiler(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).Profiler.StopProfilerAsync(cancellationToken);

    [McpServerTool(Name = "profiler-clear"),
     Description("Clear profiler samples and counters without changing the selected capture categories.")]
    public async Task<ProfilerStatusDto> ClearProfiler(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).Profiler.ClearProfilerAsync(cancellationToken);

    [McpServerTool(Name = "profiler", ReadOnly = true),
     Description(
         "Read bounded real Avalonia activity samples, retained-window aggregates, capture limits and loss counters. Missing resource Result tags remain unconfirmed outcomes.")]
    public async Task<ProfilerSnapshotDto> Profiler(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).Profiler.GetProfilerSnapshotAsync(cancellationToken);

    [McpServerTool(Name = "activity-start"),
     Description(
         "Capture bounded Avalonia logs and/or routed events. Event filter defaults to Click; '*' enables all registered routed events.")]
    public async Task<ActivityStatus> StartActivity(bool logs = true,
        bool events = false,
        long? sourceNodeId = null,
        string eventName = "Click",
        string minimumLevel = "Warning",
        string? categoryFilter = null,
        string? messageFilter = null,
        string? parameterName = null,
        string? parameterValue = null,
        CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        await client.Activity.StartActivityAsync(new ActivityStartRequest(logs,
                events,
                sourceNodeId,
                eventName,
                minimumLevel,
                new LogFilter(categoryFilter, messageFilter, parameterName, parameterValue)),
            cancellationToken);
        return await client.Activity.GetActivityStatusAsync(cancellationToken);
    }

    [McpServerTool(Name = "activity-stop"),
     Description("Stop log/event capture and release listeners, retaining the bounded history.")]
    public async Task<ActivityStatus> StopActivity(CancellationToken cancellationToken)
    {
        var client = await session.GetAsync(cancellationToken);
        await client.Activity.StopActivityAsync(cancellationToken);
        return await client.Activity.GetActivityStatusAsync(cancellationToken);
    }

    [McpServerTool(Name = "activity", ReadOnly = true),
     Description("Read up to 500 captured log/event entries, including route, handled state and source object IDs.")]
    public async Task<IReadOnlyList<ActivityEntry>> Activity(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).Activity.GetActivityAsync(cancellationToken);

    [McpServerTool(Name = "events-catalog", ReadOnly = true),
     Description(
         "Read the actual registered Avalonia routed events, stable registry identities, strategies and default event groups.")]
    public async Task<EventCatalog> EventCatalog(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).Activity.GetEventCatalogAsync(cancellationToken);

    [McpServerTool(Name = "events-start"),
     Description(
         "Capture exact registered event identities and/or event groups into correlated route entities. Without an explicit selection, uses the six configured default groups. Observations are before instance handlers; Handled changes describe intervals, not attribution to a specific handler.")]
    public async Task<ActivityStatus> StartEventRoutes(string[]? eventIds = null,
        string[]? groups = null,
        bool includeAll = false,
        long? sourceNodeId = null,
        CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        await client.Activity.StartActivityAsync(new ActivityStartRequest(false,
                true,
                sourceNodeId,
                EventSelection: new EventSelection(eventIds, groups, includeAll)),
            cancellationToken);
        return await client.Activity.GetActivityStatusAsync(cancellationToken);
    }

    [McpServerTool(Name = "events-routes", ReadOnly = true),
     Description(
         "Read bounded correlated event routes with tunnel/bubble/direct observations, strategy finishes, weak source/receiver identities and explicit truncation/expiry.")]
    public async Task<IReadOnlyList<EventRouteInfo>> EventRoutes(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).Activity.GetEventRoutesAsync(cancellationToken);

    [McpServerTool(Name = "events-status", ReadOnly = true),
     Description(
         "Read event-route count/byte/TTL limits and drop/truncation counters. Use activity-stop to stop while retaining history.")]
    public async Task<EventRouteStatus> EventRouteStatus(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).Activity.GetEventRouteStatusAsync(cancellationToken);

    [McpServerTool(Name = "breakpoints", ReadOnly = true),
     Description("List session property/event breakpoints, or read their bounded hit history when history=true.")]
    public async Task<object> Breakpoints(bool history = false, CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        return history
            ? await client.Breakpoints.GetBreakpointHistoryAsync(cancellationToken)
            : await client.Breakpoints.GetBreakpointsAsync(cancellationToken);
    }

    [McpServerTool(Name = "breakpoint-create"),
     Description(
         "Observe a property or routed event. memberId is owner-qualified, e.g. Avalonia.Controls.TextBlock:Text or Avalonia.Controls.Button:Click. Event objectId filters route receivers; sourceObjectId independently filters the actual Source. Either null is unrestricted; both filters combine. Suspend requires an attached debugger.")]
    public async Task<BreakpointInfo> CreateBreakpoint(BreakpointKind kind,
        string memberId,
        long? objectId = null,
        BreakpointComparison comparison = BreakpointComparison.Always,
        long compareTo = 1,
        bool logMessage = true,
        bool removeOnHit = false,
        bool suspend = false,
        CancellationToken cancellationToken = default,
        long? sourceObjectId = null) =>
        await (await session.GetAsync(cancellationToken)).Breakpoints.CreateBreakpointAsync(new BreakpointSpec(kind,
                memberId,
                objectId,
                Comparison: comparison,
                CompareTo: compareTo,
                LogMessage: logMessage,
                RemoveOnHit: removeOnHit,
                Suspend: suspend,
                SourceObjectId: sourceObjectId),
            cancellationToken);

    [McpServerTool(Name = "breakpoint-update"),
     Description(
         "Replace a breakpoint definition, optionally resetting its hit count. Disabling and removing release observation handlers.")]
    public async Task<BreakpointInfo> UpdateBreakpoint(long id,
        BreakpointSpec definition,
        bool resetHitCount = false,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).Breakpoints.UpdateBreakpointAsync(id,
            definition,
            resetHitCount,
            cancellationToken);

    [McpServerTool(Name = "breakpoint-remove"),
     Description("Remove a breakpoint and release its property/event observation.")]
    public async Task<string> RemoveBreakpoint(long id, CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).Breakpoints.RemoveBreakpointAsync(id, cancellationToken);
        return "Breakpoint removed";
    }
}
