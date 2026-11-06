using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class RenderingClient
{
    private readonly DiagnosticsRpcContext context;

    internal RenderingClient(DiagnosticsRpcContext context)
    {
        this.context = context;
    }

    public Task<CompositionSnapshot> GetCompositionSnapshotAsync(CompositionSnapshotRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<CompositionSnapshot>(CompositionMethods.Snapshot, request, cancellationToken);

    public Task<CompositionSnapshot> GetCompositionSnapshotAsync(long objectId,
        CancellationToken cancellationToken = default) =>
        GetCompositionSnapshotAsync(new CompositionSnapshotRequest(objectId), cancellationToken);

    public Task<RendererDiagnosticsStatusDto>
        GetRendererDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<RendererDiagnosticsStatusDto>(RendererDiagnosticsMethods.Status, null, cancellationToken);

    public Task<RendererDiagnosticsStatusDto> SetRendererDiagnosticsAsync(RendererDiagnosticsSetRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<RendererDiagnosticsStatusDto>(RendererDiagnosticsMethods.Set, request, cancellationToken);

    public Task<RendererDiagnosticsStatusDto>
        StopRendererDiagnosticsAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<RendererDiagnosticsStatusDto>(RendererDiagnosticsMethods.Stop, null, cancellationToken);

    public Task<ElementScreenshot> CaptureElementAsync(long objectId, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ElementScreenshot>(ExplorationMethods.Screenshot,
            new ObjectRequest(objectId),
            cancellationToken);

    public Task<ElementScreenshot> CaptureElementAsync(CaptureElementRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ElementScreenshot>(ExplorationMethods.Screenshot, request, cancellationToken);
}
