using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class AgentTools
{
    [McpServerTool(Name = "capture-start", ReadOnly = false, Destructive = false), Description("Start bounded metrics, profiler and/or log/event capture on the current application connection. Existing running listeners are never taken over. Returns a connection-scoped captureId and raw diagnostic snapshots; empty samples do not establish performance success.")]
    public Task<AgentCaptureResult> StartCapture(bool metrics = true, bool profiler = true, bool activity = true,
        bool logs = true, bool events = false, string eventName = "Click", string minimumLevel = "Warning",
        string[]? meterNames = null, string[]? instrumentNames = null, string[]? profilerCategories = null,
        int capacity = 512, int pollIntervalMs = 1000, CancellationToken cancellationToken = default) =>
        session.StartCaptureAsync(metrics, profiler, activity, logs, events, eventName, minimumLevel,
            meterNames, instrumentNames, profilerCategories, capacity, pollIntervalMs, cancellationToken);

    [McpServerTool(Name = "capture-status", ReadOnly = true, Destructive = false), Description("Read the raw metrics, profiler and activity snapshots for this captureId on its original application connection. Stopped snapshots are frozen. Loss counters and read errors remain explicit.")]
    public Task<AgentCaptureResult> StatusCapture(string captureId, CancellationToken cancellationToken = default) =>
        session.ReadCaptureAsync(captureId, false, cancellationToken);

    [McpServerTool(Name = "capture-stop", ReadOnly = false, Destructive = false), Description("Stop only the diagnostic listeners owned by this captureId on its original connection, then freeze its retained raw snapshots. A stale or replaced capture cannot stop a new target's listeners.")]
    public Task<AgentCaptureResult> StopCapture(string captureId, CancellationToken cancellationToken = default) =>
        session.ReadCaptureAsync(captureId, true, cancellationToken);
}
