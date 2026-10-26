using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<PopupFreezeStatusDto> GetPopupFreezeAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<PopupFreezeStatusDto>(PopupFreezeMethods.Status, null, cancellationToken);

    public Task<PopupFreezeStatusDto> SetPopupFreezeAsync(PopupFreezeSetRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<PopupFreezeStatusDto>(PopupFreezeMethods.Set, request, cancellationToken);

    public Task<PopupFreezeStatusDto> ResumePopupsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<PopupFreezeStatusDto>(PopupFreezeMethods.Resume, null, cancellationToken);
}
