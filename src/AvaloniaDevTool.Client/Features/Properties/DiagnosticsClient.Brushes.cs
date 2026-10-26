using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<OperationResult>
        SetBrushAsync(SetBrushRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<OperationResult>(BrushMethods.Set, request, cancellationToken);
}
