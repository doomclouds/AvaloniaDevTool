using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Protocol.Tests;

public sealed class TransportTests
{
    [Fact]
    public async Task Client_disconnect_cancels_inflight_server_handler_without_waiting_for_a_reply()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = await PipePair.CreateAsync(async (_, _, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            return 1;
        });
        var pending = pair.Client.InvokeAsync<int>("wait", null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pair.Client.DisposeAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pair.Server.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<RpcException>(async () => await pending);
        Assert.Equal("connection_closed", error.Code);
    }

    [Fact]
    public async Task ConcurrentRequestsRetainTheirResultsAcrossFragmentedReads()
    {
        await using var pair = await PipePair.CreateAsync((_, payload, _) =>
            Task.FromResult<object?>(ProtocolJson.Read<int>(payload) * 3), fragmentedReads: true);
        var results = await Task.WhenAll(Enumerable.Range(1, 50)
            .Select(value => pair.Client.InvokeAsync<int>("multiply", value)));
        Assert.Equal(Enumerable.Range(1, 50).Select(value => value * 3), results);
    }

    [Fact]
    public async Task RemoteErrorDoesNotCloseTheSession()
    {
        await using var pair = await PipePair.CreateAsync((method, _, _) =>
            method == "fail" ? throw new RpcException("object_expired", "The control was collected.") : Task.FromResult<object?>(42));
        var error = await Assert.ThrowsAsync<RpcException>(() => pair.Client.InvokeAsync<int>("fail", null));
        Assert.Equal("object_expired", error.Code);
        Assert.Equal(42, await pair.Client.InvokeAsync<int>("ok", null));
    }

    [Fact]
    public async Task OversizedInspectionResponseReportsAnErrorAndKeepsTheSessionUsable()
    {
        await using var pair = await PipePair.CreateAsync((method, _, _) => Task.FromResult<object?>(
            method == "large" ? new string('x', RpcPeer.MaximumFrameBytes + 1) : "small"));
        var error = await Assert.ThrowsAsync<RpcException>(() => pair.Client.InvokeAsync<string>("large", null));
        Assert.Equal("frame_too_large", error.Code);
        Assert.Equal("small", await pair.Client.InvokeAsync<string>("small", null));
    }

    [Fact]
    public async Task NotificationsDoNotConsumePendingResponses()
    {
        await using var pair = await PipePair.CreateAsync((_, _, _) => Task.FromResult<object?>(27));
        var notification = new TaskCompletionSource<SelectionChangedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Client.NotificationReceived += (method, payload) =>
        {
            if (method == Methods.SelectionChanged)
                notification.TrySetResult(ProtocolJson.Read<SelectionChangedNotification>(payload));
        };
        await pair.Server.SendNotificationAsync(Methods.SelectionChanged, new SelectionChangedNotification(17));
        Assert.Equal(17, (await notification.Task.WaitAsync(TimeSpan.FromSeconds(5))).ObjectId);
        Assert.Equal(27, await pair.Client.InvokeAsync<int>("answer", null));
    }

    [Fact]
    public async Task CancellationReleasesCallerAndLateReplyDoesNotCorruptNextRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = await PipePair.CreateAsync(async (method, _, token) =>
        {
            if (method == "slow")
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return 7;
        });
        using var cancellation = new CancellationTokenSource();
        var request = pair.Client.InvokeAsync<int>("slow", null, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
        release.TrySetResult();
        Assert.Equal(7, await pair.Client.InvokeAsync<int>("next", null));
    }

    [Fact]
    public async Task DisconnectFailsPendingRequestInsteadOfLeavingItHanging()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pair = await PipePair.CreateAsync(async (_, _, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 1;
        });
        var pending = pair.Client.InvokeAsync<int>("wait", null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pair.Server.DisposeAsync();
        var error = await Assert.ThrowsAsync<RpcException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("connection_closed", error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(RpcPeer.MaximumFrameBytes + 1)]
    public async Task InvalidFrameLengthIsRejectedBeforeAllocatingPayload(int length)
    {
        var name = "avdt-test-" + Guid.NewGuid().ToString("N");
        await using var server = NewServer(name);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        await client.ConnectAsync(5000);
        await connected;
        await using var peer = new RpcPeer(client);
        peer.Start();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await server.WriteAsync(header);
        await server.FlushAsync();
        await peer.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("invalid_frame", Assert.IsType<RpcException>(peer.CloseException).Code);
    }

    [Fact]
    public async Task ObserverFailureDoesNotTerminateTransport()
    {
        await using var pair = await PipePair.CreateAsync((_, _, _) => Task.FromResult<object?>(3));
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Client.NotificationReceived += (_, _) => throw new InvalidOperationException("Broken observer");
        pair.Client.NotificationReceived += (_, _) => notified.TrySetResult();
        await pair.Server.SendNotificationAsync("changed", new { });
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, await pair.Client.InvokeAsync<int>("ok", null));
    }

    [Fact]
    public async Task VersionMismatchIsRejectedAndClientRemainsDisconnected()
    {
        var name = "avdt-version-" + Guid.NewGuid().ToString("N");
        await using var server = NewServer(name);
        await using var peer = new RpcPeer(server, (_, _, _) => Task.FromResult<object?>(
            new SessionInfo("session", "Test", Environment.ProcessId, "12", 99)));
        await using var client = new DiagnosticsClient();
        var connect = client.ConnectAsync(name);
        await server.WaitForConnectionAsync();
        peer.Start();
        var error = await Assert.ThrowsAsync<RpcException>(async () => await connect);
        Assert.Equal("unsupported_version", error.Code);
        Assert.False(client.IsConnected);
        Assert.Null(client.Session);
    }

    [Fact]
    public async Task ClientReportsRemoteDisconnectAndCanEstablishAnotherSession()
    {
        await using var client = new DiagnosticsClient();
        for (var iteration = 0; iteration < 2; iteration++)
        {
            var name = "avdt-client-" + Guid.NewGuid().ToString("N");
            await using var server = NewServer(name);
            await using var peer = new RpcPeer(server, (_, _, _) => Task.FromResult<object?>(
                new SessionInfo("session-" + iteration, "Test", Environment.ProcessId, "12.1.4", DiagnosticsProtocol.Version)));
            var connect = client.ConnectAsync(name);
            await server.WaitForConnectionAsync();
            peer.Start();
            await connect;
            Assert.True(client.IsConnected);
            Assert.Equal("session-" + iteration, client.Session!.SessionId);
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged() { if (!client.IsConnected) disconnected.TrySetResult(); }
            client.ConnectionChanged += OnChanged;
            await peer.DisposeAsync();
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
            client.ConnectionChanged -= OnChanged;
            Assert.Null(client.Session);
        }
    }

    private static NamedPipeServerStream NewServer(string name) =>
        new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private sealed class PipePair(RpcPeer server, RpcPeer client) : IAsyncDisposable
    {
        public RpcPeer Server { get; } = server;
        public RpcPeer Client { get; } = client;
        public static async Task<PipePair> CreateAsync(Func<string, JsonElement, CancellationToken, Task<object?>> handler, bool fragmentedReads = false)
        {
            var name = "avdt-rpc-" + Guid.NewGuid().ToString("N");
            var serverStream = NewServer(name);
            var clientStream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            var connected = serverStream.WaitForConnectionAsync();
            await clientStream.ConnectAsync(5000);
            await connected;
            var server = new RpcPeer(fragmentedReads ? new FragmentingStream(serverStream) : serverStream, handler);
            var client = new RpcPeer(fragmentedReads ? new FragmentingStream(clientStream) : clientStream);
            server.Start();
            client.Start();
            return new PipePair(server, client);
        }
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }

    private sealed class FragmentingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(3, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

