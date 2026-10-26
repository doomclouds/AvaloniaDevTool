using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<ElementActionResult> PerformElementActionAsync(long objectId,
        ElementAction action,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ElementActionResult>(ExplorationMethods.Action,
            new ElementActionRequest(objectId, action),
            cancellationToken);
}
