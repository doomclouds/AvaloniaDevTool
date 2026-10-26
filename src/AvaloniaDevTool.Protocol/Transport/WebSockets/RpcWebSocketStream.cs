using System.Net.WebSockets;

namespace AvaloniaDevTool.Protocol;

/// <summary>Adapts binary WebSocket messages to the existing bounded RPC byte stream.</summary>
/// <remarks>Message boundaries are independent of RPC record boundaries. No send queue is allocated.</remarks>
public sealed class RpcWebSocketStream(WebSocket socket) : Stream
{
    public const int MaximumMessageBytes = RpcPeer.MaximumFrameBytes + sizeof(int);
    private int _messageBytes;
    private int _disposed;
    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanWrite => CanRead;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.Length == 0)
            return 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return 0;
            if (result.MessageType != WebSocketMessageType.Binary)
                throw new RpcException("invalid_frame", "Only binary WebSocket messages are supported.");
            _messageBytes = checked(_messageBytes + result.Count);
            if (_messageBytes > MaximumMessageBytes)
                throw new RpcException("invalid_frame", "The WebSocket message exceeds the protocol limit.");
            if (result.EndOfMessage)
                _messageBytes = 0;
            if (result.Count != 0)
                return result.Count;
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.Length > MaximumMessageBytes)
            throw new RpcException("frame_too_large", "The WebSocket message exceeds the protocol limit.");
        await socket.SendAsync(buffer, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            // Abort cancels pending reads/sends without waiting indefinitely for a close handshake.
            socket.Abort();
            socket.Dispose();
        }

        base.Dispose(disposing);
    }
}
