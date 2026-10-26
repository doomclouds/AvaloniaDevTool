using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using CaptureFixture = AvaloniaDevTool.Cli.Tests.AgentCaptureMcpTests.Fixture;
using HttpConnection = AvaloniaDevTool.Cli.Tests.HttpMcpIntegrationTests.HttpConnection;
using HttpHost = AvaloniaDevTool.Cli.Tests.HttpMcpIntegrationTests.HttpHost;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class HttpCaptureMcpTests
{
    [AvaloniaFact]
    public async Task Http_capture_returns_real_metrics_profiler_and_activity_then_freezes_the_stopped_result()
    {
        using var timeout = TestTimeout();
        var token = timeout.Token;
        using var fixture = new CaptureFixture();
        await using var agent = fixture.Attach();
        await using var host = await HttpHost.StartAsync(agent.PipeName, token);
        await using var connection = await HttpConnection.ConnectAsync(host.Endpoint, token);
        using var meter = NewMeter();
        const string instrumentName = "http.capture.exact";
        var counter = meter.CreateCounter<long>(instrumentName);
        var resourceKey = "HttpCapture.Resource." + Guid.NewGuid().ToString("N");
        var logMessage = "HTTP capture warning " + Guid.NewGuid().ToString("N");

        var started = await StartAllAsync(connection.Client, meter.Name, instrumentName, token);
        AssertRunning(started);
        Assert.Equal(Environment.ProcessId, started.Target.Session.ProcessId);
        Assert.Equal("same_client_connection", started.Target.OwnershipScope);
        Assert.Equal("retained_samples", started.Completeness.Scope);
        Assert.False(string.IsNullOrWhiteSpace(connection.Client.SessionId));

        counter.Add(9007199254740993L, new KeyValuePair<string, object?>("transport", "http"));
        fixture.EmitResourceLookup(resourceKey);
        AgentCaptureMcpTests.Log(logMessage);
        var current = await WaitForCaptureAsync(connection.Client,
            started.CaptureId,
            capture =>
                capture.Metrics!.Samples.Any(sample => sample.RawValue == "9007199254740993") &&
                capture.Profiler!.Samples.Any(sample =>
                    sample.Category == ProfilerCategory.ResourceLookup && sample.Key == resourceKey) &&
                capture.Activity!.Entries.Any(entry =>
                    entry.Category == "Capture.Tests" && entry.Message == logMessage),
            token);

        AssertIdentity(started, current);
        var sample = Assert.Single(current.Metrics!.Samples);
        Assert.Equal("9007199254740993", sample.RawValue);
        var instrument = Assert.Single(current.Metrics.Instruments, value => value.Id == sample.InstrumentId);
        Assert.Equal(meter.Name, instrument.MeterName);
        Assert.Equal(instrumentName, instrument.Name);
        Assert.Contains(sample.Tags, tag => tag.Key == "transport" && tag.Value == "http");
        Assert.Contains(current.Profiler!.Samples,
            value => value.Category == ProfilerCategory.ResourceLookup && value.Key == resourceKey);
        var activity = current.Activity!;
        Assert.Contains(activity.Entries,
            entry => entry.Category == "Capture.Tests" && entry.Message == logMessage &&
                     entry.Sequence > activity.AfterSequence);
        Assert.Equal(0, current.Metrics.Status.DroppedPendingSamples);
        Assert.Equal(0, current.Metrics.Status.EvictedHistorySamples);
        Assert.Equal(0, current.Profiler.Status.DroppedPendingSamples);
        Assert.Equal(0, activity.Status.DroppedCount);
        Assert.False(current.Completeness.HasDataLoss);
        Assert.False(current.Completeness.HasErrors);
        Assert.False(current.Completeness.Frozen);
        Assert.Empty(current.Issues);

        var stopped = Read<AgentCaptureResult>(await Call(connection.Client,
            "capture-stop",
            token,
            ("captureId", started.CaptureId)));
        AssertIdentity(started, stopped);
        Assert.Equal("stopped", stopped.State);
        Assert.NotNull(stopped.StoppedAt);
        Assert.True(stopped.Completeness.Frozen);
        Assert.False(stopped.Metrics!.Status.IsRunning);
        Assert.False(stopped.Profiler!.Status.IsRunning);
        Assert.False(stopped.Activity!.Status.IsRunning);

        counter.Add(42);
        fixture.EmitResourceLookup("HttpCapture.AfterStop." + Guid.NewGuid().ToString("N"));
        AgentCaptureMcpTests.Log("HTTP warning after the capture stopped");
        var frozen = Read<AgentCaptureResult>(await Call(connection.Client,
            "capture-status",
            token,
            ("captureId", started.CaptureId)));
        Assert.Equal(Serialize(stopped), Serialize(frozen));
        Assert.Equal(Serialize(stopped),
            Serialize(Read<AgentCaptureResult>(await Call(connection.Client,
                "capture-stop",
                token,
                ("captureId", started.CaptureId)))));
        Assert.All(connection.Requests, request => Assert.False(request.HasAuthorization));
        Assert.All(connection.Requests.Where(request => request.Method == "POST" && request.SessionId is not null),
            request => Assert.Equal(connection.Client.SessionId, request.SessionId));
    }

    [AvaloniaFact]
    public async Task Http_capture_reports_real_retention_loss_with_original_counters_and_raw_samples()
    {
        using var timeout = TestTimeout();
        var token = timeout.Token;
        using var fixture = new CaptureFixture();
        await using var agent = fixture.Attach();
        await using var host = await HttpHost.StartAsync(agent.PipeName, token);
        await using var connection = await HttpConnection.ConnectAsync(host.Endpoint, token);
        using var meter = NewMeter();
        var counter = meter.CreateCounter<int>("http.capture.bounded");
        var started = Read<AgentCaptureResult>(await Call(connection.Client,
            "capture-start",
            token,
            ("metrics", true),
            ("profiler", false),
            ("activity", false),
            ("meterNames", new[] { meter.Name }),
            ("instrumentNames", new[] { "http.capture.bounded" }),
            ("capacity", 2),
            ("pollIntervalMs", 100)));
        Assert.Equal("running", started.State);
        for (var value = 1; value <= 6; value++)
            counter.Add(value);

        var current = await WaitForCaptureAsync(connection.Client,
            started.CaptureId,
            capture => capture.Metrics!.Status.ReceivedSamples == 6,
            token);
        Assert.Equal(4, current.Metrics!.Status.EvictedHistorySamples);
        Assert.Equal(new[] { "5", "6" }, current.Metrics.Samples.Select(sample => sample.RawValue));
        Assert.True(current.Completeness.HasDataLoss);
        Assert.False(current.Completeness.IsComplete);
        Assert.False(current.Completeness.HasErrors);
        Assert.Null(current.Profiler);
        Assert.Null(current.Activity);
        var stopped = Read<AgentCaptureResult>(await Call(connection.Client,
            "capture-stop",
            token,
            ("captureId", started.CaptureId)));
        Assert.Equal(4, stopped.Metrics!.Status.EvictedHistorySamples);
        Assert.True(stopped.Completeness.Frozen);
        Assert.All(connection.Requests, request => Assert.False(request.HasAuthorization));
    }

    [AvaloniaFact]
    public async Task
        Http_session_delete_releases_running_capture_and_old_ids_cannot_read_or_stop_the_next_sessions_capture()
    {
        using var timeout = TestTimeout();
        var token = timeout.Token;
        using var fixture = new CaptureFixture();
        await using var agent = fixture.Attach();
        await using var host = await HttpHost.StartAsync(agent.PipeName, token);
        using var meter = NewMeter();
        var oldCounter = meter.CreateCounter<int>("http.capture.old");
        var nextCounter = meter.CreateCounter<int>("http.capture.next");
        await using var first = await HttpConnection.ConnectAsync(host.Endpoint, token);
        var oldSessionId = first.Client.SessionId;
        var previous = await StartAllAsync(first.Client, meter.Name, "http.capture.old", token);
        AssertRunning(previous);
        oldCounter.Add(13);
        var oldResource = "HttpCapture.OldResource." + Guid.NewGuid().ToString("N");
        var oldMessage = "HTTP capture before DELETE " + Guid.NewGuid().ToString("N");
        fixture.EmitResourceLookup(oldResource);
        AgentCaptureMcpTests.Log(oldMessage);
        await WaitForCaptureAsync(first.Client,
            previous.CaptureId,
            capture =>
                capture.Metrics!.Samples.Any(sample => sample.RawValue == "13") &&
                capture.Profiler!.Samples.Any(sample => sample.Key == oldResource) &&
                capture.Activity!.Entries.Any(entry => entry.Message == oldMessage),
            token);

        await first.DisposeAsync();
        await WaitForDisconnectedAsync(agent, token);
        Assert.Contains(first.Requests, request => request.Method == "DELETE" && request.SessionId == oldSessionId);
        Assert.All(first.Requests, request => Assert.False(request.HasAuthorization));

        await using var next = await HttpConnection.ConnectAsync(host.Endpoint, token);
        Assert.NotEqual(oldSessionId, next.Client.SessionId);
        var started = await StartAllAsync(next.Client, meter.Name, "http.capture.next", token);
        AssertRunning(started);
        Assert.NotEqual(previous.CaptureId, started.CaptureId);
        Assert.NotEqual(previous.Target.ConnectionId, started.Target.ConnectionId);
        Assert.NotEqual(previous.Target.Session.SessionId, started.Target.Session.SessionId);
        Assert.NotEqual(previous.Metrics!.Status.CaptureId, started.Metrics!.Status.CaptureId);
        Assert.NotEqual(previous.Profiler!.Status.CaptureId, started.Profiler!.Status.CaptureId);
        Error(await Call(next.Client, "capture-status", token, ("captureId", previous.CaptureId)), "capture_not_found");
        Error(await Call(next.Client, "capture-stop", token, ("captureId", previous.CaptureId)), "capture_not_found");

        nextCounter.Add(17);
        var nextResource = "HttpCapture.NextResource." + Guid.NewGuid().ToString("N");
        var nextMessage = "HTTP capture after DELETE " + Guid.NewGuid().ToString("N");
        fixture.EmitResourceLookup(nextResource);
        AgentCaptureMcpTests.Log(nextMessage);
        var current = await WaitForCaptureAsync(next.Client,
            started.CaptureId,
            capture =>
                capture.Metrics!.Samples.Any(sample => sample.RawValue == "17") &&
                capture.Profiler!.Samples.Any(sample => sample.Key == nextResource) &&
                capture.Activity!.Entries.Any(entry => entry.Message == nextMessage),
            token);
        AssertRunning(current);
        AssertIdentity(started, current);
        Assert.DoesNotContain(current.Metrics!.Samples, sample => sample.RawValue == "13");
        Assert.DoesNotContain(current.Profiler!.Samples, sample => sample.Key == oldResource);
        Assert.DoesNotContain(current.Activity!.Entries, entry => entry.Message == oldMessage);
        Assert.All(next.Requests, request => Assert.False(request.HasAuthorization));
        await next.DisposeAsync();
        await WaitForDisconnectedAsync(agent, token);
    }

    private static async Task<AgentCaptureResult> StartAllAsync(McpClient client,
        string meter,
        string instrument,
        CancellationToken token) => Read<AgentCaptureResult>(await Call(client,
        "capture-start",
        token,
        ("metrics", true),
        ("profiler", true),
        ("activity", true),
        ("logs", true),
        ("events", false),
        ("meterNames", new[] { meter }),
        ("instrumentNames", new[] { instrument }),
        ("profilerCategories", new[] { "ResourceLookup" }),
        ("capacity", 64),
        ("pollIntervalMs", 100)));

    private static void AssertRunning(AgentCaptureResult capture)
    {
        Assert.Equal("running", capture.State);
        Assert.False(string.IsNullOrWhiteSpace(capture.CaptureId));
        Assert.False(string.IsNullOrWhiteSpace(capture.Target.ConnectionId));
        Assert.True(capture.Metrics!.Status.IsRunning);
        Assert.True(capture.Profiler!.Status.IsRunning);
        Assert.True(capture.Activity!.Status.IsRunning);
        Assert.False(string.IsNullOrWhiteSpace(capture.Metrics.Status.CaptureId));
        Assert.False(string.IsNullOrWhiteSpace(capture.Profiler.Status.CaptureId));
        Assert.Empty(capture.Issues);
    }

    private static void AssertIdentity(AgentCaptureResult started, AgentCaptureResult current)
    {
        Assert.Equal(started.CaptureId, current.CaptureId);
        Assert.Equal(started.Target.ConnectionId, current.Target.ConnectionId);
        Assert.Equal(started.Target.Session.SessionId, current.Target.Session.SessionId);
        Assert.Equal(started.Metrics!.Status.CaptureId, current.Metrics!.Status.CaptureId);
        Assert.Equal(started.Profiler!.Status.CaptureId, current.Profiler!.Status.CaptureId);
    }

    private static async Task<AgentCaptureResult> WaitForCaptureAsync(McpClient client,
        string captureId,
        Func<AgentCaptureResult, bool> predicate,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        AgentCaptureResult? last = null;
        try
        {
            while (true)
            {
                last = Read<AgentCaptureResult>(await Call(client,
                    "capture-status",
                    timeout.Token,
                    ("captureId", captureId)));
                if (predicate(last))
                    return last;
                await Task.Delay(25, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("HTTP capture did not receive the real emitted diagnostics. Last result: " +
                                       Serialize(last));
        }
    }

    private static async Task WaitForDisconnectedAsync(DiagnosticsAgent agent, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (agent.IsConnected)
                await Task.Delay(25, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("HTTP DELETE did not release the Agent capture session. " + agent.LastError);
        }
    }

    private static CancellationTokenSource TestTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        return timeout;
    }

    private static Meter NewMeter() => new("HttpCapture.Tests." + Guid.NewGuid().ToString("N"));
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ProtocolJson.Options);

    private static ValueTask<CallToolResult> Call(McpClient client,
        string name,
        CancellationToken token,
        params (string Name, object? Value)[] values) => client.CallToolAsync(name,
        values.ToDictionary(value => value.Name, value => value.Value),
        cancellationToken: token);

    private static T Read<T>(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var root = result.StructuredContent is { } content
            ? JsonNode.Parse(JsonSerializer.Serialize(content))
            : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }

    private static void Error(CallToolResult result, string code)
    {
        Assert.True(result.IsError, Text(result));
        Assert.Contains(code, Text(result));
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
