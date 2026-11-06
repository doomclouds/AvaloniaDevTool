using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class StylesClient
{
    private readonly DiagnosticsRpcContext context;

    internal StylesClient(DiagnosticsRpcContext context)
    {
        this.context = context;
    }

    public Task<IReadOnlyList<PseudoClassInfo>>
        GetPseudoClassesAsync(long objectId, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<PseudoClassInfo>>(ExplorationMethods.PseudoClasses,
            new ObjectRequest(objectId),
            cancellationToken);

    public async Task
        SetPseudoClassAsync(long objectId, string name, bool enabled, CancellationToken cancellationToken = default) =>
        _ = await context.InvokeAsync<OperationResult>(ExplorationMethods.SetPseudoClass,
            new SetPseudoClassRequest(objectId, name, enabled),
            cancellationToken).ConfigureAwait(false);

    public async Task
        ResetPseudoClassAsync(long objectId, string name, CancellationToken cancellationToken = default) => _ =
        await context.InvokeAsync<OperationResult>(ExplorationMethods.ResetPseudoClass,
            new ResetPseudoClassRequest(objectId, name),
            cancellationToken).ConfigureAwait(false);

    public Task<StyleSnapshot> GetStylesAsync(long objectId, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<StyleSnapshot>(InspectionMethods.Styles, new ObjectRequest(objectId), cancellationToken);
}
