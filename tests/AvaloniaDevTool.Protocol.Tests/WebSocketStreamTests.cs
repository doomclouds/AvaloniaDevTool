using System.IO.Pipes;
using System.Net.WebSockets;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Protocol.Tests;

public sealed class WebSocketStreamTests
{
    [Fact]
    public async Task Fragments_and_separate_messages_are_read_as_one_rpc_byte_stream()
    {
        await using var pair = await SocketPair.CreateAsync();
        using var stream = new RpcWebSocketStream(pair.Server);
        var bytes = new byte[7];
        var reading = stream.ReadExactlyAsync(bytes).AsTask();
        await pair.Client.SendAsync(new byte[] { 1, 2 }, WebSocketMessageType.Binary, false, CancellationToken.None);
        await pair.Client.SendAsync(new byte[] { 3, 4 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        await pair.Client.SendAsync(new byte[] { 5, 6, 7 }, WebSocketMessageType.Binary, true, CancellationToken.None);
        await reading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, bytes);
    }

    [Fact]
    public async Task Fragmented_oversized_websocket_message_is_rejected_without_accumulating_a_message_buffer()
    {
        await using var pair = await SocketPair.CreateAsync();
        using var stream = new RpcWebSocketStream(pair.Server);
        var reading = ReadUntilErrorAsync(stream);
        var chunk = new byte[64 * 1024];
        var sending = Task.Run(async () =>
        {
            try
            {
                for (var offset = 0; offset < RpcWebSocketStream.MaximumMessageBytes; offset += chunk.Length)
                    await pair.Client.SendAsync(chunk, WebSocketMessageType.Binary, false, CancellationToken.None);
            }
            catch (WebSocketException) { }
            catch (IOException) { }
        });
        var error = await Assert.ThrowsAsync<RpcException>(() => reading.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("invalid_frame", error.Code);
        stream.Dispose();
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Close_frame_returns_end_of_stream()
    {
        await using var pair = await SocketPair.CreateAsync();
        using var stream = new RpcWebSocketStream(pair.Server);
        var reading = stream.ReadAsync(new byte[10]).AsTask();
        await pair.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        Assert.Equal(0, await reading.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Bidirectional_rpc_can_receive_an_answer_while_its_request_handler_is_pending()
    {
        await using var pair = await SocketPair.CreateAsync();
        RpcPeer? server = null;
        await using var client = new RpcPeer(new RpcWebSocketStream(pair.Client), (_, _, _) => Task.FromResult<object?>(6));
        await using var serverPeer = new RpcPeer(new RpcWebSocketStream(pair.Server), async (_, _, token) =>
            await server!.InvokeAsync<int>("callback", null, token) * 7);
        server = serverPeer;
        client.Start();
        serverPeer.Start();
        Assert.Equal(42, await client.InvokeAsync<int>("request", null).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task ReadUntilErrorAsync(Stream stream)
    {
        var buffer = new byte[4096];
        while (await stream.ReadAsync(buffer) != 0) { }
    }

    private sealed class SocketPair(WebSocket server, WebSocket client) : IAsyncDisposable
    {
        public WebSocket Server { get; } = server;
        public WebSocket Client { get; } = client;
        public static async Task<SocketPair> CreateAsync()
        {
            var name = "avdt-websocket-" + Guid.NewGuid().ToString("N");
            var serverPipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var clientPipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await Task.WhenAll(serverPipe.WaitForConnectionAsync(), clientPipe.ConnectAsync());
            return new SocketPair(WebSocket.CreateFromStream(serverPipe, true, null, Timeout.InfiniteTimeSpan),
                WebSocket.CreateFromStream(clientPipe, false, null, Timeout.InfiniteTimeSpan));
        }
        public ValueTask DisposeAsync()
        {
            Server.Dispose(); Client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
