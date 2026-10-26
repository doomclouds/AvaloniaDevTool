using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<RendererDiagnosticsStatusDto>
        GetRendererDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<RendererDiagnosticsStatusDto>(RendererDiagnosticsMethods.Status, null, cancellationToken);

    public Task<RendererDiagnosticsStatusDto> SetRendererDiagnosticsAsync(RendererDiagnosticsSetRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<RendererDiagnosticsStatusDto>(RendererDiagnosticsMethods.Set, request, cancellationToken);

    public Task<RendererDiagnosticsStatusDto> StopRendererDiagnosticsAsync(CancellationToken cancellationToken =
        default) =>
        InvokeAsync<RendererDiagnosticsStatusDto>(RendererDiagnosticsMethods.Stop, null, cancellationToken);
}
