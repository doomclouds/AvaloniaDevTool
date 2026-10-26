using System.Text.Json;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public event Action<BreakpointHit>? BreakpointHit;
    public event Action<BreakpointChangedNotification>? BreakpointsChanged;

    public Task<BreakpointInfo> CreateBreakpointAsync(BreakpointSpec definition,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<BreakpointInfo>(BreakpointMethods.Create,
            new CreateBreakpointRequest(definition),
            cancellationToken);

    public Task<BreakpointInfo> UpdateBreakpointAsync(long id,
        BreakpointSpec definition,
        bool resetHitCount = false,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<BreakpointInfo>(BreakpointMethods.Update,
            new UpdateBreakpointRequest(id, definition, resetHitCount),
            cancellationToken);

    public async Task RemoveBreakpointAsync(long id, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(BreakpointMethods.Remove,
            new RemoveBreakpointRequest(id),
            cancellationToken).ConfigureAwait(false);

    public Task<IReadOnlyList<BreakpointInfo>> GetBreakpointsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<BreakpointInfo>>(BreakpointMethods.List, null, cancellationToken);

    public Task<BreakpointHistory> GetBreakpointHistoryAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<BreakpointHistory>(BreakpointMethods.History, null, cancellationToken);

    private bool HandleBreakpointNotification(string method, JsonElement payload)
    {
        switch (method)
        {
            case BreakpointMethods.Hit:
                BreakpointHit?.Invoke(ProtocolJson.Read<BreakpointHit>(payload));
                return true;
            case BreakpointMethods.Changed:
                BreakpointsChanged?.Invoke(ProtocolJson.Read<BreakpointChangedNotification>(payload));
                return true;
            default:
                return false;
        }
    }
}
