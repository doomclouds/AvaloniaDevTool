using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class SelectionClient
{
    private readonly DiagnosticsRpcContext context;

    internal SelectionClient(DiagnosticsRpcContext context)
    {
        this.context = context;
        context.Subscribe((method, payload) =>
        {
            switch (method)
            {
                case Methods.SelectionChanged:
                    SelectionChanged?.Invoke(ProtocolJson.Read<SelectionChangedNotification>(payload));
                    return true;
                case Methods.PickingChanged:
                    PickingChanged?.Invoke(ProtocolJson.Read<PickingChangedNotification>(payload));
                    return true;
                default:
                    return false;
            }
        });
    }

    public event Action<SelectionChangedNotification>? SelectionChanged;
    public event Action<PickingChangedNotification>? PickingChanged;

    public async Task HighlightAsync(long? objectId, CancellationToken cancellationToken = default) => _ =
        await context.InvokeAsync<OperationResult>(Methods.Highlight, new HighlightRequest(objectId), cancellationToken)
            .ConfigureAwait(false);

    public async Task SetPickingAsync(bool enabled, CancellationToken cancellationToken = default) => _ =
        await context.InvokeAsync<OperationResult>(Methods.SetPicking, new PickingRequest(enabled), cancellationToken)
            .ConfigureAwait(false);

    public async Task SetFollowFocusAsync(bool enabled, CancellationToken cancellationToken = default) => _ =
        await context
            .InvokeAsync<OperationResult>(ExplorationMethods.FollowFocus,
                new FollowFocusRequest(enabled),
                cancellationToken).ConfigureAwait(false);
}
