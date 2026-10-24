using System.Text.Json;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    public async Task<object?> HandleAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (method == CompositionMethods.Snapshot)
            return await _composition.CaptureAsync(ProtocolJson.Read<CompositionSnapshotRequest>(parameters), cancellationToken);
        return Handle(method, parameters);
    }
}
