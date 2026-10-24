namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private Task NotifyActivityAsync(string method, object payload, CancellationToken cancellationToken) =>
        _peer is { } peer && !_disposed
            ? peer.SendNotificationAsync(method, payload, cancellationToken)
            : Task.CompletedTask;
}
