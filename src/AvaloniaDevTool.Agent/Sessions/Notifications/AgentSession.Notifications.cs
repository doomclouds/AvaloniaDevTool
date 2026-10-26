using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private RpcPeer? _peer;

    private void Notify(string method, object parameters)
    {
        if (_peer is { } peer && !_disposed)
            _ = SendNotificationAsync(peer, method, parameters);
    }

    private static async Task SendNotificationAsync(RpcPeer peer, string method, object parameters)
    {
        try
        {
            await peer.SendNotificationAsync(method, parameters);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException
                                       or RpcException)
        {
        }
    }
}
