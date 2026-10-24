using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Protocol.Tests;

public sealed class RpcTraceTests
{
    [Fact]
    public async Task A_received_frame_is_not_a_success_when_typed_result_decoding_fails()
    {
        await using var pair = await Pair.CreateAsync((_, _, _) => Task.FromResult<object?>("not an integer"));
        var buffer = new RpcTraceBuffer(); buffer.Start(true);
        pair.Client.TraceObserved += observation => buffer.Observe("a", observation);
        await Assert.ThrowsAsync<JsonException>(() => pair.Client.InvokeAsync<int>("wrong-result", null));
        var entry = Assert.Single(buffer.Snapshot().Entries);
        Assert.Equal("ClientFailed", entry.State); Assert.Equal("invalid_response", entry.ErrorCode);
        Assert.Contains("not an integer", entry.ResponseJson);
    }
    [Fact]
    public async Task Actual_duplex_frames_correlate_opposite_ids_errors_notifications_and_redact_payloads()
    {
        await using var pair = await Pair.CreateAsync((method, payload, _) => method == "fail"
            ? throw new RpcException("object_expired", "Gone") : Task.FromResult<object?>(payload),
            (_, _, _) => Task.FromResult<object?>(23));
        var buffer = new RpcTraceBuffer(); buffer.Start(true);
        pair.Client.TraceObserved += _ => throw new InvalidOperationException("Observer failure");
        pair.Client.TraceObserved += observation => buffer.Observe("connection-a", observation);
        var echoed = await pair.Client.InvokeAsync<JsonElement>("echo", new { token = "never-record-this", nested = new { Password = "hidden", text = "中文\n\"" } });
        Assert.Equal("never-record-this", echoed.GetProperty("token").GetString());
        Assert.Equal(23, await pair.Server.InvokeAsync<int>("callback", new { value = 4 }));
        await Assert.ThrowsAsync<RpcException>(() => pair.Client.InvokeAsync<int>("fail", null));
        var notification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Client.NotificationReceived += (_, _) => notification.TrySetResult();
        await pair.Server.SendNotificationAsync("changed", new { id = 17 });
        await notification.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var entries = buffer.Snapshot().Entries;
        var outbound = Assert.Single(entries, entry => entry.Method == "echo");
        Assert.Equal("Succeeded", outbound.State); Assert.True(outbound.DurationMs >= 0);
        Assert.True(outbound.SentBytes > 0); Assert.True(outbound.ReceivedBytes > 0);
        Assert.Contains("[redacted]", outbound.RequestJson); Assert.DoesNotContain("never-record-this", outbound.RequestJson);
        Assert.DoesNotContain("hidden", outbound.ResponseJson);
        var callback = Assert.Single(entries, entry => entry.Method == "callback");
        Assert.Equal(outbound.RequestId, callback.RequestId); Assert.Equal("Received", callback.Direction);
        Assert.Equal("Succeeded", callback.State);
        var failed = Assert.Single(entries, entry => entry.Method == "fail");
        Assert.Equal("Failed", failed.State); Assert.Equal("object_expired", failed.ErrorCode);
        Assert.Contains(entries, entry => entry.Method == "changed" && entry.Kind == "Notification" && entry.State == "Observed");
    }

    [Fact]
    public async Task Caller_cancellation_and_late_response_preserve_identity_and_do_not_poison_next_call()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = await Pair.CreateAsync(async (method, _, token) =>
        {
            if (method == "slow") { entered.TrySetResult(); await release.Task.WaitAsync(token); }
            return 7;
        });
        var buffer = new RpcTraceBuffer(); buffer.Start();
        pair.Client.TraceObserved += observation => buffer.Observe("a", observation);
        using var cancellation = new CancellationTokenSource();
        var pending = pair.Client.InvokeAsync<int>("slow", null, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal("CancelledOrFailed", Assert.Single(buffer.Snapshot().Entries).State);
        release.TrySetResult();
        Assert.Equal(7, await pair.Client.InvokeAsync<int>("next", null));
        var snapshot = buffer.Snapshot();
        var slow = Assert.Single(snapshot.Entries, entry => entry.Method == "slow");
        Assert.Equal("LateResponse", slow.State); Assert.Equal("request_cancelled", slow.ErrorCode);
        Assert.Null(slow.RequestJson); Assert.Null(slow.ResponseJson);
        Assert.Equal("Succeeded", Assert.Single(snapshot.Entries, entry => entry.Method == "next").State);
    }

    [Fact]
    public void Connection_ids_stop_and_clear_define_explicit_correlation_boundaries()
    {
        var buffer = new RpcTraceBuffer(); buffer.Start();
        buffer.Observe("old", Request(1)); buffer.Observe("new", Request(1));
        buffer.Observe("old", new(RpcTraceKind.Response, RpcTraceDirection.Received, 1, null, null, 10));
        buffer.Observe("new", new(RpcTraceKind.ConnectionClosed, RpcTraceDirection.Local, null, null, null, 0));
        Assert.Equal("Succeeded", Assert.Single(buffer.Snapshot().Entries, entry => entry.ConnectionId == "old").State);
        Assert.Equal("Disconnected", Assert.Single(buffer.Snapshot().Entries, entry => entry.ConnectionId == "new").State);
        buffer.Stop(); var before = buffer.Snapshot(); buffer.Observe("ignored", Request(2));
        Assert.Equal(before.Revision, buffer.Snapshot().Revision);
        buffer.Start(true); buffer.Observe("a", Request(3)); buffer.Clear();
        buffer.Observe("a", new(RpcTraceKind.Response, RpcTraceDirection.Received, 3, null, null, 11));
        Assert.Equal("Uncorrelated", Assert.Single(buffer.Snapshot().Entries).State);
        Assert.True(buffer.Snapshot().IsRecording);
        buffer.Observe("a", Request(4)); buffer.Stop();
        Assert.Contains(buffer.Snapshot().Entries, entry => entry.State == "CaptureStopped");
    }

    [Fact]
    public void Unicode_payload_projection_and_history_are_bounded_and_valid_json()
    {
        var buffer = new RpcTraceBuffer(); buffer.Start(true);
        var payload = JsonSerializer.SerializeToElement(Enumerable.Range(0, 400).Select(_ => new string('中', 512) + "\"\\"));
        for (var i = 1; i <= 600; i++) buffer.Observe("a", Request(i) with { Payload = payload });
        var snapshot = buffer.Snapshot();
        Assert.InRange(snapshot.Entries.Count, 1, RpcTraceBuffer.MaximumEntries);
        Assert.InRange(snapshot.BufferedBytes, 1, RpcTraceBuffer.MaximumBufferedBytes);
        Assert.True(snapshot.EvictedCount > 0);
        Assert.All(snapshot.Entries, entry =>
        {
            Assert.True(entry.PayloadTruncated);
            Assert.InRange(Encoding.UTF8.GetByteCount(entry.RequestJson!), 1, RpcTraceBuffer.MaximumPayloadBytes);
            using var json = JsonDocument.Parse(entry.RequestJson!); Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        });
    }

    private static RpcTraceObservation Request(long id) => new(RpcTraceKind.Request, RpcTraceDirection.Sent, id, "test", null, 20);
    private sealed record Pair(RpcPeer Server, RpcPeer Client) : IAsyncDisposable
    {
        public static async Task<Pair> CreateAsync(Func<string, JsonElement, CancellationToken, Task<object?>> handler,
            Func<string, JsonElement, CancellationToken, Task<object?>>? clientHandler = null)
        {
            var name = "trace-" + Guid.NewGuid().ToString("N");
            var serverStream = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var clientStream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var connected = serverStream.WaitForConnectionAsync(); await clientStream.ConnectAsync(5000); await connected;
            var pair = new Pair(new(serverStream, handler), new(clientStream, clientHandler));
            pair.Server.Start(); pair.Client.Start(); return pair;
        }
        public async ValueTask DisposeAsync() { await Client.DisposeAsync(); await Server.DisposeAsync(); }
    }
}
