using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<ElementScreenshot> CaptureElementAsync(long objectId, CancellationToken cancellationToken = default) =>
        InvokeAsync<ElementScreenshot>(ExplorationMethods.Screenshot, new ObjectRequest(objectId), cancellationToken);

    public Task<ElementScreenshot> CaptureElementAsync(CaptureElementRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ElementScreenshot>(ExplorationMethods.Screenshot, request, cancellationToken);
}
