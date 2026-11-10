using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Styling;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class AgentCaptureMcpTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task Default_stdio_capture_tools_return_raw_diagnostics_and_freeze_the_stopped_snapshot()
    {
        using var fixture = new Fixture();
        await using var agent = fixture.Attach();
        await using var mcp = await CreateClientAsync(agent.PipeName);
        var listed = await mcp.ListToolsAsync(cancellationToken: Token);
        foreach (var name in new[] { "capture-start", "capture-status", "capture-stop" })
            Assert.Contains(listed, tool => tool.Name == name);

        using var meter = NewMeter();
        var counter = meter.CreateCounter<long>("capture.exact");
        var started = Read<AgentCaptureResult>(await Call(mcp, "capture-start", ("meterNames", new[] { meter.Name })));
        Assert.Equal("running", started.State);
        Assert.False(string.IsNullOrWhiteSpace(started.CaptureId));
        Assert.Equal(Environment.ProcessId, started.Target.Session.ProcessId);
        Assert.True(started.Metrics!.Status.IsRunning);
        Assert.True(started.Profiler!.Status.IsRunning);
        Assert.True(started.Activity!.Status.IsRunning);

        counter.Add(9007199254740993L);
        fixture.EmitResourceLookup("CaptureRawResource");
        Log("Capture raw warning");
        var current = Read<AgentCaptureResult>(await Call(mcp, "capture-status", ("captureId", started.CaptureId)));
        Assert.Equal("9007199254740993", Assert.Single(current.Metrics!.Samples).RawValue);
        Assert.Contains(current.Profiler!.Samples,
            sample => sample.Category == ProfilerCategory.ResourceLookup && sample.Key == "CaptureRawResource");
        Assert.Contains(current.Activity!.Entries,
            entry => entry.Category == "Capture.Tests" && entry.Message == "Capture raw warning");

        var stopped = Read<AgentCaptureResult>(await Call(mcp, "capture-stop", ("captureId", started.CaptureId)));
        Assert.Equal("stopped", stopped.State);
        Assert.NotNull(stopped.StoppedAt);
        Assert.False(stopped.Metrics!.Status.IsRunning);
        Assert.False(stopped.Profiler!.Status.IsRunning);
        Assert.False(stopped.Activity!.Status.IsRunning);
        counter.Add(42);
        fixture.EmitResourceLookup("AfterStoppedCapture");
        Log("After stopped capture");
        var frozen = Read<AgentCaptureResult>(await Call(mcp, "capture-status", ("captureId", started.CaptureId)));
        Assert.Equal(Serialize(stopped), Serialize(frozen));
        Assert.Equal(Serialize(stopped),
            Serialize(Read<AgentCaptureResult>(await Call(mcp, "capture-stop", ("captureId", started.CaptureId)))));
    }

    [AvaloniaTheory]
    [InlineData("metrics")]
    [InlineData("profiler")]
    [InlineData("activity")]
    public async Task All_selected_services_are_preflighted_before_starting_or_resetting_any_capture(string running)
    {
        using var fixture = new Fixture();
        await using var agent = fixture.Attach();
        await using var session = new ToolSession(agent.PipeName);
        var tools = CreateTools(session);
        var client = await session.GetAsync(Token);
        using var meter = NewMeter();
        var counter = meter.CreateCounter<int>("preserved");
        switch (running)
        {
            case "metrics":
                await client.Metrics.StartMetricsAsync(new MetricsStartRequest(MeterNames: [meter.Name]), Token);
                counter.Add(17);
                break;
            case "profiler":
                await client.Profiler.StartProfilerAsync(
                    new ProfilerStartRequest(Categories: [ProfilerCategory.ResourceLookup]),
                    Token);
                fixture.EmitResourceLookup("PreflightPreserved");
                break;
            case "activity":
                await client.Activity.StartActivityAsync(new ActivityStartRequest(MinimumLogLevel: "Error"), Token);
                Log("Preflight retained activity", LogEventLevel.Error);
                break;
        }

        var beforeMetrics = await client.Metrics.GetMetricsSnapshotAsync(Token);
        var beforeProfiler = await client.Profiler.GetProfilerSnapshotAsync(Token);
        var beforeActivity = await client.Activity.GetActivityStatusAsync(Token);
        await Assert.ThrowsAsync<McpException>(() => tools.StartCapture(cancellationToken: Token));
        var afterMetrics = await client.Metrics.GetMetricsSnapshotAsync(Token);
        var afterProfiler = await client.Profiler.GetProfilerSnapshotAsync(Token);
        var afterActivity = await client.Activity.GetActivityStatusAsync(Token);
        Assert.Equal(running == "metrics", afterMetrics.Status.IsRunning);
        Assert.Equal(running == "profiler", afterProfiler.Status.IsRunning);
        Assert.Equal(running == "activity", afterActivity.IsRunning);
        Assert.Equal(beforeMetrics.Status.CaptureId, afterMetrics.Status.CaptureId);
        Assert.Equal(beforeProfiler.Status.CaptureId, afterProfiler.Status.CaptureId);
        Assert.Equal(beforeMetrics.Samples.Select(sample => sample.RawValue),
            afterMetrics.Samples.Select(sample => sample.RawValue));
        Assert.Equal(beforeProfiler.Samples.Select(sample => sample.Sequence),
            afterProfiler.Samples.Select(sample => sample.Sequence));
        Assert.Equal(beforeActivity.MinimumLogLevel, afterActivity.MinimumLogLevel);
        if (running == "activity")
            Assert.Contains(await client.Activity.GetActivityAsync(Token),
                entry => entry.Message == "Preflight retained activity");
    }

    [AvaloniaTheory]
    [InlineData("no-service")]
    [InlineData("invalid-category")]
    [InlineData("invalid-log-level")]
    [InlineData("invalid-capacity")]
    [InlineData("invalid-meter-filter")]
    public async Task Invalid_combined_requests_do_not_partially_start_diagnostics(string invalid)
    {
        using var fixture = new Fixture();
        await using var agent = fixture.Attach();
        await using var session = new ToolSession(agent.PipeName);
        var tools = CreateTools(session);
        var client = await session.GetAsync(Token);
        await Assert.ThrowsAsync<McpException>(() => invalid switch
        {
            "no-service" => tools.StartCapture(metrics: false,
                profiler: false,
                activity: false,
                cancellationToken: Token),
            "invalid-category" => tools.StartCapture(profilerCategories: ["MissingCategory"], cancellationToken: Token),
            "invalid-log-level" => tools.StartCapture(minimumLevel: "MissingLevel", cancellationToken: Token),
            "invalid-capacity" => tools.StartCapture(capacity: 0, cancellationToken: Token),
            "invalid-meter-filter" => tools.StartCapture(meterNames: [""], cancellationToken: Token),
            _ => throw new InvalidOperationException(invalid)
        });
        Assert.False((await client.Metrics.GetMetricsStatusAsync(Token)).IsRunning);
        Assert.False((await client.Profiler.GetProfilerStatusAsync(Token)).IsRunning);
        Assert.False((await client.Activity.GetActivityStatusAsync(Token)).IsRunning);
    }

    [AvaloniaFact]
    public async Task New_runs_get_unique_ids_and_exclude_activity_retained_by_a_previous_run()
    {
        using var fixture = new Fixture();
        await using var agent = fixture.Attach();
        await using var session = new ToolSession(agent.PipeName);
        var tools = CreateTools(session);
        var first = await tools.StartCapture(metrics: false, profiler: false, cancellationToken: Token);
        Log("First capture only");
        var firstStopped = await tools.StopCapture(first.CaptureId, Token);
        Assert.Contains(firstStopped.Activity!.Entries, entry => entry.Message == "First capture only");
        var second = await tools.StartCapture(metrics: false, profiler: false, cancellationToken: Token);
        Assert.NotEqual(first.CaptureId, second.CaptureId);
        Assert.Equal(first.Target.ConnectionId, second.Target.ConnectionId);
        Log("Second capture only");
        var secondStopped = await tools.StopCapture(second.CaptureId, Token);
        Assert.Contains(secondStopped.Activity!.Entries, entry => entry.Message == "Second capture only");
        Assert.DoesNotContain(secondStopped.Activity.Entries, entry => entry.Message == "First capture only");
        Assert.Equal(Serialize(firstStopped), Serialize(await tools.StatusCapture(first.CaptureId, Token)));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reconnect_and_target_switch_reject_old_ids_without_stopping_the_new_capture(bool differentTarget)
    {
        using var fixture = new Fixture();
        await using var firstAgent = fixture.Attach();
        await using var secondAgent = fixture.Attach();
        await using var session = new ToolSession(firstAgent.PipeName);
        var tools = CreateTools(session);
        var first = await tools.StartCapture(profiler: false, activity: false, cancellationToken: Token);
        await session.AttachAsync(differentTarget ? secondAgent.PipeName : firstAgent.PipeName, Token);
        var next = await tools.StartCapture(profiler: false, activity: false, cancellationToken: Token);
        Assert.NotEqual(first.Target.ConnectionId, next.Target.ConnectionId);
        var client = await session.GetAsync(Token);
        await Assert.ThrowsAsync<McpException>(() => tools.StatusCapture(first.CaptureId, Token));
        await Assert.ThrowsAsync<McpException>(() => tools.StopCapture(first.CaptureId, Token));
        var remaining = await client.Metrics.GetMetricsStatusAsync(Token);
        Assert.True(remaining.IsRunning);
        Assert.Equal(next.Metrics!.Status.CaptureId, remaining.CaptureId);
        Assert.Equal("stopped", (await tools.StopCapture(next.CaptureId, Token)).State);
    }

    [AvaloniaTheory]
    [InlineData("metrics")]
    [InlineData("profiler")]
    [InlineData("activity")]
    public async Task Externally_restarted_service_is_never_stopped_by_the_previous_capture(string service)
    {
        using var fixture = new Fixture();
        await using var agent = fixture.Attach();
        await using var session = new ToolSession(agent.PipeName);
        var tools = CreateTools(session);
        var capture = await tools.StartCapture(metrics: service == "metrics",
            profiler: service == "profiler",
            activity: service == "activity",
            cancellationToken: Token);
        var client = await session.GetAsync(Token);
        switch (service)
        {
            case "metrics":
                await client.Metrics.StartMetricsAsync(new MetricsStartRequest(MeterNames: ["ExternalOwner"]), Token);
                break;
            case "profiler":
                await client.Profiler.StartProfilerAsync(new ProfilerStartRequest(Filter: "ExternalOwner"), Token);
                break;
            // Identical settings also constitute a new owner's capture.
            case "activity":
                await client.Activity.StartActivityAsync(new ActivityStartRequest(EventName: "Click"), Token);
                break;
        }

        var stopped = await tools.StopCapture(capture.CaptureId, Token);
        Assert.NotEqual("stopped", stopped.State);
        Assert.NotEmpty(stopped.Issues);
        Assert.True(service switch
        {
            "metrics" => (await client.Metrics.GetMetricsStatusAsync(Token)).IsRunning,
            "profiler" => (await client.Profiler.GetProfilerStatusAsync(Token)).IsRunning,
            "activity" => (await client.Activity.GetActivityStatusAsync(Token)).IsRunning,
            _ => false
        });
    }

    [AvaloniaFact]
    public async Task Retained_window_loss_is_reported_with_the_original_counters()
    {
        using var fixture = new Fixture();
        await using var agent = fixture.Attach();
        await using var session = new ToolSession(agent.PipeName);
        var tools = CreateTools(session);
        using var meter = NewMeter();
        var counter = meter.CreateCounter<int>("bounded");
        var capture = await tools.StartCapture(profiler: false,
            activity: false,
            meterNames: [meter.Name],
            capacity: 2,
            cancellationToken: Token);
        for (var index = 1; index <= 6; index++)
            counter.Add(index);
        var stopped = await tools.StopCapture(capture.CaptureId, Token);
        Assert.Equal(4, stopped.Metrics!.Status.EvictedHistorySamples);
        Assert.Equal(["5", "6"], stopped.Metrics.Samples.Select(sample => sample.RawValue));
        Assert.True(stopped.Completeness.HasDataLoss);
        Assert.False(stopped.Completeness.IsComplete);
    }

    [Fact]
    public async Task Partial_start_failure_rolls_back_only_the_services_started_by_this_run()
    {
        await using var endpoint = new FaultEndpoint(failProfilerStart: true);
        await using var session = new ToolSession(endpoint.PipeName);
        var tools = CreateTools(session);
        var capture = await tools.StartCapture(cancellationToken: Token);
        Assert.Equal("failed", capture.State);
        Assert.NotEmpty(capture.Issues);
        Assert.False(capture.Completeness.IsComplete);
        Assert.False(endpoint.Metrics.IsRunning);
        Assert.Single(endpoint.Requests, method => method == MetricsMethods.Start);
        Assert.Single(endpoint.Requests, method => method == MetricsMethods.Stop);
        Assert.Single(endpoint.Requests, method => method == ProfilerMethods.Start);
        Assert.DoesNotContain(ProfilerMethods.Stop, endpoint.Requests);
        Assert.DoesNotContain(ActivityMethods.Start, endpoint.Requests);
        Assert.DoesNotContain(ActivityMethods.Stop, endpoint.Requests);
    }

    [Fact]
    public async Task Stop_rpc_failure_preserves_the_running_status_and_reports_incomplete_results()
    {
        await using var endpoint = new FaultEndpoint(failMetricsStop: true);
        await using var session = new ToolSession(endpoint.PipeName);
        var tools = CreateTools(session);
        var started = await tools.StartCapture(profiler: false, activity: false, cancellationToken: Token);
        Assert.Equal("running", started.State);
        var stopped = await tools.StopCapture(started.CaptureId, Token);
        Assert.NotEqual("stopped", stopped.State);
        Assert.True(stopped.Metrics!.Status.IsRunning);
        Assert.True(endpoint.Metrics.IsRunning);
        Assert.True(stopped.Completeness.HasErrors);
        Assert.False(stopped.Completeness.IsComplete);
        Assert.NotEmpty(stopped.Issues);
    }

    [Fact]
    public async Task Conditional_mutation_rejects_a_changed_epoch_without_sending_stop()
    {
        await using var endpoint = new FaultEndpoint();
        await using var session = new ToolSession(endpoint.PipeName);
        var client = await session.GetAsync(Token);
        await client.Metrics.StartMetricsAsync(new MetricsStartRequest(), Token);
        var expected = ((IDiagnosticCaptureClient)client).GetDiagnosticMutationEpoch(DiagnosticCaptureService.Metrics);
        var replacement = await client.Metrics.StartMetricsAsync(new MetricsStartRequest(), Token);
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            ((IDiagnosticCaptureClient)client).RunOwnedDiagnosticMutationAsync(
                DiagnosticCaptureService.Metrics,
                expected,
                () => client.Metrics.StopMetricsAsync(Token)));
        Assert.Equal("capture_ownership_lost", error.Code);
        Assert.DoesNotContain(MetricsMethods.Stop, endpoint.Requests);
        Assert.True(endpoint.Metrics.IsRunning);
        Assert.Equal(replacement.CaptureId, endpoint.Metrics.CaptureId);
    }

    [Fact]
    public async Task Conditional_mutation_releases_its_gate_while_the_response_task_is_pending()
    {
        await using var endpoint = new FaultEndpoint();
        await using var session = new ToolSession(endpoint.PipeName);
        var client = await session.GetAsync(Token);
        var expected = ((IDiagnosticCaptureClient)client).GetDiagnosticMutationEpoch(DiagnosticCaptureService.Metrics);
        var responseReady =
            new TaskCompletionSource<MetricsStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = ((IDiagnosticCaptureClient)client).RunOwnedDiagnosticMutationAsync(
            DiagnosticCaptureService.Metrics,
            expected,
            async () =>
            {
                var response = await client.Metrics.StartMetricsAsync(new MetricsStartRequest(), Token);
                responseReady.TrySetResult(response);
                await releaseResponse.Task.WaitAsync(Token);
                return response;
            });
        try
        {
            var first = await responseReady.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            var replacement = await client.Metrics.StartMetricsAsync(new MetricsStartRequest(), Token)
                .WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.False(original.IsCompleted);
            Assert.NotEqual(first.CaptureId, replacement.CaptureId);
            Assert.Equal(expected + 2,
                ((IDiagnosticCaptureClient)client).GetDiagnosticMutationEpoch(DiagnosticCaptureService.Metrics));
            releaseResponse.TrySetResult();
            Assert.Equal(first.CaptureId, (await original).CaptureId);
            var error = await Assert.ThrowsAsync<RpcException>(() =>
                ((IDiagnosticCaptureClient)client).RunOwnedDiagnosticMutationAsync(
                    DiagnosticCaptureService.Metrics,
                    expected + 1,
                    () => client.Metrics.StopMetricsAsync(Token)));
            Assert.Equal("capture_ownership_lost", error.Code);
            Assert.DoesNotContain(MetricsMethods.Stop, endpoint.Requests);
            Assert.True(endpoint.Metrics.IsRunning);
            Assert.Equal(replacement.CaptureId, endpoint.Metrics.CaptureId);
        }
        finally
        {
            releaseResponse.TrySetResult();
        }
    }

    private static CaptureTools CreateTools(ToolSession session) => new(session, new ManagedToolContext(session));

    private static Meter NewMeter() => new("Capture.Tests." + Guid.NewGuid().ToString("N"));

    internal static void Log(string message, LogEventLevel level = LogEventLevel.Warning) =>
        Logger.Sink?.Log(level, "Capture.Tests", null, message);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ProtocolJson.Options);

    private static Task<McpClient> CreateClientAsync(string pipe)
    {
        var cliPath = typeof(AgentCaptureMcpTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(item => item.Key == "PreviewCliPath").Value!;
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet", Name = "Agent diagnostics capture stdio tests",
                Arguments = [cliPath, "mcp", "--pipe", pipe], ShutdownTimeout = TimeSpan.FromSeconds(5)
            }),
            cancellationToken: Token);
    }

    private static ValueTask<CallToolResult> Call(McpClient client,
        string name,
        params (string Name, object? Value)[] values) =>
        client.CallToolAsync(name,
            values.ToDictionary(value => value.Name, value => value.Value),
            cancellationToken: Token);

    private static T Read<T>(CallToolResult result)
    {
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.False(result.IsError == true, text);
        var root = result.StructuredContent is { } content
            ? JsonNode.Parse(JsonSerializer.Serialize(content))
            : JsonNode.Parse(text);
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly Button _button = new() { Width = 180, Height = 60, Content = "Capture target" };
        private readonly Window _window;

        public Fixture()
        {
            _window = new Window { Width = 320, Height = 200, Content = _button };
            _window.Show();
            _window.UpdateLayout();
        }

        public DiagnosticsAgent Attach() => DiagnosticsAgent.Attach(Application.Current!,
            new DiagnosticsAgentOptions
            {
                PipeName = DiagnosticsProtocol.PipePrefix + "capture-tests-" + Guid.NewGuid().ToString("N"),
                ApplicationName = "Agent capture tests", TopLevelProvider = () => [_window]
            });

        public void EmitResourceLookup(string key)
        {
            _window.Resources[key] = "captured";
            Assert.True(_button.TryFindResource(key, out var value));
            Assert.Equal("captured", value);
        }

        public void Dispose() => _window.Close();
    }

    private sealed class FaultEndpoint : IAsyncDisposable
    {
        private readonly bool _failProfilerStart;
        private readonly bool _failMetricsStop;
        private readonly NamedPipeServerStream _pipe;
        private readonly RpcPeer _peer;
        private readonly Task _connected;

        private readonly SessionInfo _session = new(Guid.NewGuid().ToString("N"),
            "Capture fault endpoint",
            Environment.ProcessId,
            "12.1.4",
            DiagnosticsProtocol.Version);

        private readonly ProfilerStatusDto _profiler = Empty<ProfilerStatusDto>();
        private readonly ActivityStatus _activity = new(false, false, false, 0, 0, "Warning", "Click", null);

        public FaultEndpoint(bool failProfilerStart = false, bool failMetricsStop = false)
        {
            _failProfilerStart = failProfilerStart;
            _failMetricsStop = failMetricsStop;
            PipeName = "capture-fault-tests-" + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _peer = new RpcPeer(_pipe, HandleAsync);
            _connected = ConnectAsync();
        }

        public string PipeName { get; }
        public ConcurrentQueue<string> Requests { get; } = new();
        public MetricsStatusDto Metrics { get; private set; } = Empty<MetricsStatusDto>();

        private async Task ConnectAsync()
        {
            await _pipe.WaitForConnectionAsync(Token);
            _peer.Start();
        }

        private Task<object?> HandleAsync(string method, JsonElement payload, CancellationToken token)
        {
            Requests.Enqueue(method);
            object result;
            switch (method)
            {
                case Methods.SessionInfo:
                    result = _session;
                    break;
                case MetricsMethods.Status:
                    result = Metrics;
                    break;
                case MetricsMethods.Snapshot:
                    result = new MetricsSnapshotDto(Metrics, [], [], []);
                    break;
                case MetricsMethods.Start:
                    Metrics = Metrics with { IsRunning = true, CaptureId = Guid.NewGuid().ToString("N") };
                    result = Metrics;
                    break;
                case MetricsMethods.Stop:
                    if (_failMetricsStop)
                        throw new RpcException("fixture_stop_failed",
                            "The fixture metrics listener could not be stopped.");
                    Metrics = Metrics with { IsRunning = false };
                    result = Metrics;
                    break;
                case ProfilerMethods.Status:
                    result = _profiler;
                    break;
                case ProfilerMethods.Snapshot:
                    result = new ProfilerSnapshotDto(_profiler, [], []);
                    break;
                case ProfilerMethods.Start:
                    if (_failProfilerStart)
                        throw new RpcException("fixture_start_failed",
                            "The fixture profiler listener could not be started.");
                    throw new InvalidOperationException("Profiler start was not expected.");
                case ActivityMethods.Status:
                    result = _activity;
                    break;
                case ActivityMethods.Snapshot:
                    result = Array.Empty<ActivityEntry>();
                    break;
                default:
                    throw new RpcException("unknown_method", "Unexpected fixture request: " + method);
            }

            return Task.FromResult<object?>(result);
        }

        private static T Empty<T>() => JsonSerializer.Deserialize<T>("{}", ProtocolJson.Options)!;

        public async ValueTask DisposeAsync()
        {
            await _peer.DisposeAsync();
            await _connected;
        }
    }
}
