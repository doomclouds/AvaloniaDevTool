using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed class SessionNotifications : IDisposable
{
    private RpcPeer? _peer;
    internal void Connect(RpcPeer peer) => _peer = peer;

    internal void Publish(string method, object payload)
    {
        if (_peer is { } peer)
            _ = SendIgnoringDisconnectAsync(peer, method, payload);
    }

    internal Task SendAsync(string method, object payload, CancellationToken cancellation) =>
        _peer is { } peer ? peer.SendNotificationAsync(method, payload, cancellation) : Task.CompletedTask;

    private static async Task SendIgnoringDisconnectAsync(RpcPeer peer, string method, object payload)
    {
        try
        {
            await peer.SendNotificationAsync(method, payload);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException
                                          or RpcException)
        {
        }
    }

    public void Dispose() => _peer = null;
}
