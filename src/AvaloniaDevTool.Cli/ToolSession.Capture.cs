using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;

namespace AvaloniaDevTool.Cli;

public sealed partial class ToolSession
{
    private readonly Dictionary<string, CaptureRun> _captures = new(StringComparer.Ordinal);
    private SessionInfo? _captureConnection;
    private string _captureConnectionId = "";

    internal async Task<AgentCaptureResult> StartCaptureAsync(bool metrics, bool profiler, bool activity,
        bool logs, bool events, string eventName, string minimumLevel, string[]? meterNames,
        string[]? instrumentNames, string[]? profilerCategories, int capacity, int pollIntervalMs,
        CancellationToken cancellationToken)
    {
        if (!metrics && !profiler && !activity)
            throw new McpException("invalid_argument: Select at least one diagnostic service.");
        if (capacity is < 1 or > 1024 || pollIntervalMs is < 100 or > 60000)
            throw new McpException("invalid_argument: capacity must be 1..1024 and pollIntervalMs 100..60000.");
        ValidateCapturePatterns(meterNames);
        ValidateCapturePatterns(instrumentNames);
        var categories = profilerCategories?.Select(value =>
            Enum.TryParse<ProfilerCategory>(value, true, out var category) && Enum.IsDefined(category)
                ? category : throw new McpException("invalid_argument: profilerCategories must be StyleAttach, StyleEvaluate or ResourceLookup.")).ToArray();
        if (categories is { Length: 0 or > 3 })
            throw new McpException("invalid_argument: Select 1..3 profiler categories.");
        if (activity && (!logs && !events || eventName?.Length > 256 ||
            !new[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" }.Contains(minimumLevel, StringComparer.OrdinalIgnoreCase)))
            throw new McpException("invalid_argument: Select an activity source, a valid minimumLevel, and an eventName of at most 256 characters.");

        var client = await GetAsync(cancellationToken).ConfigureAwait(false);
        await _attachment.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identity = client.Session ?? throw new McpException("not_connected: Attach to an application before capturing diagnostics.");
            if (!ReferenceEquals(identity, _captureConnection))
            {
                _captureConnection = identity;
                _captureConnectionId = Guid.NewGuid().ToString("N");
            }
            if (_captures.Count >= 64)
                throw new McpException("capture_limit: This MCP session retains at most 64 captures. Restart the MCP session to release them.");
            var selected = new[] { metrics, profiler, activity };
            var epochs = Enum.GetValues<DiagnosticCaptureService>().Select(client.GetDiagnosticMutationEpoch).ToArray();
            // Preflight every selected service before starting any listener.
            var metricStatus = metrics ? await client.GetMetricsStatusAsync(cancellationToken).ConfigureAwait(false) : null;
            var profilerStatus = profiler ? await client.GetProfilerStatusAsync(cancellationToken).ConfigureAwait(false) : null;
            var activityStatus = activity ? await client.GetActivityStatusAsync(cancellationToken).ConfigureAwait(false) : null;
            if (metricStatus?.IsRunning == true || profilerStatus?.IsRunning == true || activityStatus?.IsRunning == true)
                throw new McpException("capture_already_running: A selected diagnostic service is already running. Stop its owner explicitly before starting a new capture.");
            var activityHistory = activity ? await client.GetActivityAsync(cancellationToken).ConfigureAwait(false) : [];
            if (activity) activityStatus = await client.GetActivityStatusAsync(cancellationToken).ConfigureAwait(false);
            if (activityStatus?.IsRunning == true)
                throw new McpException("capture_already_running: Activity capture started during preflight.");
            VerifyCaptureConnection(identity);
            var run = new CaptureRun(Guid.NewGuid().ToString("N"), new(_captureConnectionId, identity), selected, epochs,
                activityStatus, activityHistory.Count == 0 ? 0 : activityHistory.Max(entry => entry.Sequence));
            _captures.Add(run.Id, run);
            try
            {
                if (metrics)
                {
                    var status = await StartOwnedCaptureMutation(client, run, DiagnosticCaptureService.Metrics,
                        () => client.StartMetricsAsync(new MetricsStartRequest(MeterNames: meterNames,
                            InstrumentNames: instrumentNames, Capacity: capacity, PollIntervalMs: pollIntervalMs), cancellationToken)).ConfigureAwait(false);
                    run.MetricsId = status.CaptureId;
                    run.Started[0] = status.IsRunning;
                    run.Running[0] = status.IsRunning;
                    if (!status.IsRunning) throw new RpcException("capture_start_failed", "Metrics did not enter the running state.");
                }
                if (profiler)
                {
                    var status = await StartOwnedCaptureMutation(client, run, DiagnosticCaptureService.Profiler,
                        () => client.StartProfilerAsync(new ProfilerStartRequest(categories, Capacity: capacity), cancellationToken)).ConfigureAwait(false);
                    run.ProfilerId = status.CaptureId;
                    run.Started[1] = status.IsRunning;
                    run.Running[1] = status.IsRunning;
                    if (!status.IsRunning) throw new RpcException("capture_start_failed", "Profiler did not enter the running state.");
                }
                if (activity)
                {
                    await StartOwnedCaptureMutation(client, run, DiagnosticCaptureService.Activity, async () =>
                    {
                        await client.StartActivityAsync(new ActivityStartRequest(logs, events, EventName: eventName,
                            MinimumLogLevel: minimumLevel), cancellationToken).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
                    run.Started[2] = true;
                    run.Running[2] = true;
                }
                await SnapshotCaptureAsync(client, run, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return CaptureResult(run);
            }
            catch (Exception error)
            {
                run.FailedStart = true;
                AddCaptureIssue(run, "capture", "start_failed", CaptureError(error));
                // Cancellation must not prevent cleanup of listeners already started by this run.
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await StopOwnedCaptureAsync(client, run, rollback.Token).ConfigureAwait(false);
                await SnapshotCaptureAsync(client, run, rollback.Token).ConfigureAwait(false);
                run.Frozen = !run.Running.Any(value => value);
                if (run.Frozen) run.StoppedAt = DateTimeOffset.UtcNow;
                return CaptureResult(run);
            }
        }
        catch (RpcException error) { throw new McpException($"{error.Code}: {error.Message}"); }
        finally { _attachment.Release(); }
    }

    internal async Task<AgentCaptureResult> ReadCaptureAsync(string captureId, bool stop, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(captureId)) throw new McpException("invalid_argument: captureId is required.");
        await _attachment.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_captures.TryGetValue(captureId, out var run)) throw new McpException("capture_not_found: This captureId was not created by this MCP session.");
            VerifyCaptureConnection(run.Target.Session);
            if (run.Frozen) return CaptureResult(run);
            if (stop) await StopOwnedCaptureAsync(_client, run, cancellationToken).ConfigureAwait(false);
            await SnapshotCaptureAsync(_client, run, cancellationToken).ConfigureAwait(false);
            if (stop && !run.Running.Any(value => value))
            {
                run.Frozen = true;
                run.StoppedAt = DateTimeOffset.UtcNow;
            }
            return CaptureResult(run);
        }
        finally { _attachment.Release(); }
    }

    private static Task<T> StartOwnedCaptureMutation<T>(DiagnosticsClient client, CaptureRun run,
        DiagnosticCaptureService service, Func<Task<T>> mutation)
    {
        var index = (int)service;
        var before = run.Epochs[index];
        var task = client.RunOwnedDiagnosticMutationAsync(service, before, mutation);
        run.Epochs[index] = before + 1;
        return task;
    }

    private async Task StopOwnedCaptureAsync(DiagnosticsClient client, CaptureRun run, CancellationToken token)
    {
        foreach (var service in Enum.GetValues<DiagnosticCaptureService>())
        {
            var index = (int)service;
            if (!run.Started[index] || run.Stopped[index] || run.Lost[index]) continue;
            try
            {
                VerifyCaptureConnection(run.Target.Session);
                if (!HasCaptureOwnership(client, run, service)) continue;
                var running = service switch
                {
                    DiagnosticCaptureService.Metrics => CheckMetricsOwnership(run, await client.GetMetricsStatusAsync(token).ConfigureAwait(false), true),
                    DiagnosticCaptureService.Profiler => CheckProfilerOwnership(run, await client.GetProfilerStatusAsync(token).ConfigureAwait(false), true),
                    _ => (await client.GetActivityStatusAsync(token).ConfigureAwait(false)).IsRunning
                };
                if (run.Lost[index]) continue;
                if (!running)
                {
                    LoseCaptureOwnership(run, service, "The owned listener stopped outside this capture.");
                    continue;
                }
                var expected = run.Epochs[index];
                // The client gate makes the ownership check and mutation registration atomic for all same-client callers.
                var stopTask = client.RunOwnedDiagnosticMutationAsync(service, expected, async () =>
                {
                    switch (service)
                    {
                        case DiagnosticCaptureService.Metrics: await client.StopMetricsAsync(token).ConfigureAwait(false); break;
                        case DiagnosticCaptureService.Profiler: await client.StopProfilerAsync(token).ConfigureAwait(false); break;
                        default: await client.StopActivityAsync(token).ConfigureAwait(false); break;
                    }
                    return true;
                });
                run.Epochs[index] = expected + 1;
                await stopTask.ConfigureAwait(false);
                run.Stopped[index] = true;
                run.Running[index] = false;
            }
            catch (RpcException error) when (error.Code == "capture_ownership_lost")
            {
                LoseCaptureOwnership(run, service, error.Message);
            }
            catch (Exception error)
            {
                run.Running[index] = true;
                AddCaptureIssue(run, service.ToString(), "stop_failed", CaptureError(error));
            }
        }
    }

    private async Task SnapshotCaptureAsync(DiagnosticsClient client, CaptureRun run, CancellationToken token)
    {
        foreach (var service in Enum.GetValues<DiagnosticCaptureService>())
        {
            var index = (int)service;
            if (!run.Started[index] || run.Lost[index]) continue;
            try
            {
                VerifyCaptureConnection(run.Target.Session);
                if (!HasCaptureOwnership(client, run, service)) continue;
                switch (service)
                {
                    case DiagnosticCaptureService.Metrics:
                        var metrics = await client.GetMetricsSnapshotAsync(token).ConfigureAwait(false);
                        VerifyCaptureConnection(run.Target.Session);
                        if (!HasCaptureOwnership(client, run, service) || !CheckMetricsOwnership(run, metrics.Status)) continue;
                        run.Metrics = metrics;
                        run.Running[index] = metrics.Status.IsRunning;
                        break;
                    case DiagnosticCaptureService.Profiler:
                        var profiler = await client.GetProfilerSnapshotAsync(token).ConfigureAwait(false);
                        VerifyCaptureConnection(run.Target.Session);
                        if (!HasCaptureOwnership(client, run, service) || !CheckProfilerOwnership(run, profiler.Status)) continue;
                        run.Profiler = profiler;
                        run.Running[index] = profiler.Status.IsRunning;
                        break;
                    default:
                        var entries = await client.GetActivityAsync(token).ConfigureAwait(false);
                        var status = await client.GetActivityStatusAsync(token).ConfigureAwait(false);
                        VerifyCaptureConnection(run.Target.Session);
                        if (!HasCaptureOwnership(client, run, service)) continue;
                        run.Activity = new(status, entries.Where(entry => entry.Sequence > run.AfterSequence).ToArray(),
                            run.ActivityBaseline!, run.AfterSequence);
                        run.Running[index] = status.IsRunning;
                        break;
                }
                VerifyCaptureConnection(run.Target.Session);
                if (!run.Stopped[index] && !run.Running[index])
                    LoseCaptureOwnership(run, service, "The listener stopped outside this capture.");
                if (run.Stopped[index] && run.Running[index])
                    LoseCaptureOwnership(run, service, "The listener restarted after capture-stop.");
            }
            catch (Exception error)
            {
                AddCaptureIssue(run, service.ToString(), "snapshot_failed", CaptureError(error));
            }
        }
    }

    private static bool HasCaptureOwnership(DiagnosticsClient client, CaptureRun run, DiagnosticCaptureService service)
    {
        if (client.GetDiagnosticMutationEpoch(service) == run.Epochs[(int)service]) return true;
        LoseCaptureOwnership(run, service, "Another start, stop or clear request changed this service on the same client.");
        return false;
    }

    private static bool CheckMetricsOwnership(CaptureRun run, MetricsStatusDto status, bool requireRunning = false)
    {
        if (status.CaptureId == run.MetricsId) return !requireRunning || status.IsRunning;
        LoseCaptureOwnership(run, DiagnosticCaptureService.Metrics, "The target metrics captureId changed.");
        return false;
    }

    private static bool CheckProfilerOwnership(CaptureRun run, ProfilerStatusDto status, bool requireRunning = false)
    {
        if (status.CaptureId == run.ProfilerId) return !requireRunning || status.IsRunning;
        LoseCaptureOwnership(run, DiagnosticCaptureService.Profiler, "The target profiler captureId changed.");
        return false;
    }

    private static void LoseCaptureOwnership(CaptureRun run, DiagnosticCaptureService service, string message)
    {
        run.Lost[(int)service] = true;
        run.Running[(int)service] = false;
        AddCaptureIssue(run, service.ToString(), "capture_ownership_lost", message);
    }

    private void VerifyCaptureConnection(SessionInfo identity)
    {
        if (!_client.IsConnected || !ReferenceEquals(_client.Session, identity))
            throw new McpException("stale_capture: This capture belongs to a previous application connection. Its listeners cannot be accessed through the current target.");
    }

    private static AgentCaptureResult CaptureResult(CaptureRun run)
    {
        var metrics = run.Metrics?.Status;
        var profiler = run.Profiler?.Status;
        var activity = run.Activity?.Status;
        var baseline = run.ActivityBaseline;
        var loss = metrics is not null && (metrics.DroppedPendingSamples > 0 || metrics.EvictedHistorySamples > 0 ||
                metrics.DroppedSeriesSamples > 0 || metrics.DroppedInstruments > 0 || metrics.TruncatedTagSets > 0 ||
                metrics.InvalidMeasurements > 0 || metrics.ExpiredHistorySamples > 0) ||
            profiler is not null && (profiler.EvictedHistorySamples > 0 || profiler.DroppedPendingSamples > 0 ||
                profiler.DroppedGroupSamples > 0 || profiler.DroppedInFlightOperations > 0 ||
                profiler.IncompleteOperations > 0 || profiler.TruncatedTagSets > 0) ||
            activity is not null && baseline is not null && (activity.DroppedCount > baseline.DroppedCount ||
                activity.ByteEvictedCount > baseline.ByteEvictedCount || activity.BudgetTruncatedCount > baseline.BudgetTruncatedCount ||
                activity.NotificationDroppedCount > baseline.NotificationDroppedCount);
        var errors = run.Issues.Count > 0 || metrics is { ObservableErrors: > 0 } or { NotificationErrors: > 0 } ||
            metrics?.LastError is not null || profiler is { CallbackErrors: > 0 } or { NotificationErrors: > 0 } || profiler?.LastError is not null;
        var available = (!run.Selected[0] || run.Metrics is not null) && (!run.Selected[1] || run.Profiler is not null) &&
            (!run.Selected[2] || run.Activity is not null);
        var state = run.FailedStart ? "failed" : run.Lost.Any(value => value) ? "lost" :
            errors ? "failed" : run.Frozen ? "stopped" : "running";
        return new(run.Id, run.Target, state, run.StartedAt, run.StoppedAt, run.Metrics, run.Profiler, run.Activity,
            new(available && !loss && !errors, loss, errors, run.Frozen), run.Issues.ToArray());
    }

    private static void AddCaptureIssue(CaptureRun run, string service, string code, string message)
    {
        if (!run.Issues.Any(issue => issue.Service == service && issue.Code == code && issue.Message == message))
            run.Issues.Add(new(service, code, message));
    }

    private static string CaptureError(Exception error) => error is RpcException rpc ? $"{rpc.Code}: {rpc.Message}" : error.Message;

    private static void ValidateCapturePatterns(string[]? patterns)
    {
        if (patterns is not null && (patterns.Length > 16 || patterns.Any(pattern => string.IsNullOrWhiteSpace(pattern) || pattern.Length > 128)))
            throw new McpException("invalid_argument: Name filters accept at most 16 nonempty patterns of at most 128 characters.");
    }

    private sealed class CaptureRun(string id, AgentCaptureTarget target, bool[] selected, long[] epochs,
        ActivityStatus? baseline, long afterSequence)
    {
        public string Id { get; } = id;
        public AgentCaptureTarget Target { get; } = target;
        public bool[] Selected { get; } = selected;
        public long[] Epochs { get; } = epochs;
        public bool[] Started { get; } = new bool[3];
        public bool[] Stopped { get; } = new bool[3];
        public bool[] Running { get; } = new bool[3];
        public bool[] Lost { get; } = new bool[3];
        public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? StoppedAt { get; set; }
        public ActivityStatus? ActivityBaseline { get; } = baseline;
        public long AfterSequence { get; } = afterSequence;
        public string? MetricsId { get; set; }
        public string? ProfilerId { get; set; }
        public MetricsSnapshotDto? Metrics { get; set; }
        public ProfilerSnapshotDto? Profiler { get; set; }
        public AgentActivitySnapshot? Activity { get; set; }
        public List<AgentCaptureIssue> Issues { get; } = [];
        public bool FailedStart { get; set; }
        public bool Frozen { get; set; }
    }
}
