using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class PopupsClient
{
    private readonly DiagnosticsRpcContext context;

    internal PopupsClient(DiagnosticsRpcContext context)
    {
        this.context = context;
    }

    public Task<PopupFreezeStatusDto> GetPopupFreezeAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<PopupFreezeStatusDto>(PopupFreezeMethods.Status, null, cancellationToken);

    public Task<PopupFreezeStatusDto> SetPopupFreezeAsync(PopupFreezeSetRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<PopupFreezeStatusDto>(PopupFreezeMethods.Set, request, cancellationToken);

    public Task<PopupFreezeStatusDto> ResumePopupsAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<PopupFreezeStatusDto>(PopupFreezeMethods.Resume, null, cancellationToken);
}
