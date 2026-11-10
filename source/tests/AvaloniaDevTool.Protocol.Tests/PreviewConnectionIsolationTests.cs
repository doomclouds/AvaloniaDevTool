using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Protocol.Tests;

public sealed class PreviewConnectionIsolationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_old_generation_read_cannot_update_the_reconnected_application(bool failedRead)
    {
        await using var connection = new DiagnosticsConnection();
        var delayedRead = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = new Queue<Task<long>>([delayedRead.Task, Task.FromResult(3L)]);
        var preview = new PreviewClient(connection, _ => reads.Dequeue());
        await using var first = new TargetPeer("first", 40);
        await first.ConnectAsync(connection);
        Assert.Equal(40, preview.CurrentContentGeneration);

        // Hold the read completion at the capability boundary while the real transport reconnects.
        var oldRead = preview.RefreshContentGenerationAsync();
        await using var second = new TargetPeer("second", 2);
        await second.ConnectAsync(connection);
        Assert.Equal(2, preview.CurrentContentGeneration);

        if (failedRead)
            delayedRead.SetException(new RpcException("unknown_method", "The generation read failed."));
        else
            delayedRead.SetResult(999);

        var error = await Assert.ThrowsAsync<RpcException>(() => oldRead);
        Assert.Equal("stale_session", error.Code);
        Assert.Equal("second", connection.Session!.SessionId);
        Assert.Equal(2, preview.CurrentContentGeneration);
        Assert.Equal(3, await preview.RefreshContentGenerationAsync());
    }

    [Fact]
    public async Task A_current_connections_generation_error_is_propagated_without_a_fallback_value()
    {
        await using var connection = new DiagnosticsConnection();
        var expected = new RpcException("unknown_method", "The generation endpoint is unavailable.");
        var preview = new PreviewClient(connection, _ => Task.FromException<long>(expected));
        await using var target = new TargetPeer("current", 4);
        await target.ConnectAsync(connection);

        var error = await Assert.ThrowsAsync<RpcException>(() => preview.RefreshContentGenerationAsync());

        Assert.Same(expected, error);
        Assert.Equal(4, preview.CurrentContentGeneration);
    }

    [Fact]
    public async Task A_retired_peers_captured_notification_callback_cannot_reach_current_capabilities()
    {
        var context = new DiagnosticsRpcContext();
        await using var connection = context.Connection;
        var selection = new SelectionClient(context);
        var preview = new PreviewClient(connection, _ => Task.FromResult(0L));
        var selected = new List<long>();
        var liveNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        selection.SelectionChanged += notification =>
        {
            selected.Add(notification.ObjectId);
            liveNotification.TrySetResult();
        };
        await using var first = new TargetPeer("first", 40);
        await first.ConnectAsync(connection);

        // RpcPeer copies its invocation list before calling subscribers. Preserve that exact
        // producer-side snapshot to model delivery racing with unsubscribe and a new connection.
        var field = typeof(RpcPeer).GetField("NotificationReceived", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var oldCallback = Assert.IsAssignableFrom<Action<string, JsonElement>>(field.GetValue(context.Peer));
        await using var second = new TargetPeer("second", 2);
        await second.ConnectAsync(connection);

        oldCallback(Methods.SelectionChanged, ProtocolJson.ToElement(new SelectionChangedNotification(999)));
        oldCallback(PreviewMethods.ContentGenerationChanged, ProtocolJson.ToElement(999L));

        Assert.Empty(selected);
        Assert.Equal(2, preview.CurrentContentGeneration);
        await second.Peer.SendNotificationAsync(Methods.SelectionChanged,
            new SelectionChangedNotification(7));
        await liveNotification.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([7L], selected);
    }

    private sealed class TargetPeer : IAsyncDisposable
    {
        private readonly string pipeName = "avdt-preview-isolation-" + Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream stream;

        internal TargetPeer(string sessionId, long generation)
        {
            stream = new NamedPipeServerStream(pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var info = new SessionInfo(sessionId,
                "Preview isolation",
                Environment.ProcessId,
                "12.1.4",
                DiagnosticsProtocol.Version,
                generation);
            Peer = new RpcPeer(stream, (_, _, _) => Task.FromResult<object?>(info));
        }

        internal RpcPeer Peer { get; }

        internal async Task ConnectAsync(DiagnosticsConnection connection)
        {
            var connecting = connection.ConnectAsync(pipeName);
            await stream.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Peer.Start();
            await connecting;
        }

        public ValueTask DisposeAsync() => Peer.DisposeAsync();
    }
}
