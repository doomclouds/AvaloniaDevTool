using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class BreakpointsClient
{
    private readonly DiagnosticsRpcContext context;

    internal BreakpointsClient(DiagnosticsRpcContext context)
    {
        this.context = context;
        context.Subscribe((method, payload) =>
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
        });
    }

    public event Action<BreakpointHit>? BreakpointHit;
    public event Action<BreakpointChangedNotification>? BreakpointsChanged;

    public Task<BreakpointInfo>
        CreateBreakpointAsync(BreakpointSpec definition, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<BreakpointInfo>(BreakpointMethods.Create,
            new CreateBreakpointRequest(definition),
            cancellationToken);

    public Task<BreakpointInfo> UpdateBreakpointAsync(long id,
        BreakpointSpec definition,
        bool resetHitCount = false,
        CancellationToken cancellationToken = default) => context.InvokeAsync<BreakpointInfo>(BreakpointMethods.Update,
        new UpdateBreakpointRequest(id, definition, resetHitCount),
        cancellationToken);

    public async Task RemoveBreakpointAsync(long id, CancellationToken cancellationToken = default) => _ =
        await context
            .InvokeAsync<OperationResult>(BreakpointMethods.Remove, new RemoveBreakpointRequest(id), cancellationToken)
            .ConfigureAwait(false);

    public Task<IReadOnlyList<BreakpointInfo>> GetBreakpointsAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<BreakpointInfo>>(BreakpointMethods.List, null, cancellationToken);

    public Task<BreakpointHistory> GetBreakpointHistoryAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<BreakpointHistory>(BreakpointMethods.History, null, cancellationToken);
}
