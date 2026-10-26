using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<TreeObservationResult> ObserveTreeAsync(long objectId,
        TreeKind kind,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<TreeObservationResult>(TreeWorkflowMethods.Observe,
            new TreeObserveRequest(objectId, kind),
            cancellationToken);

    public Task<TreeObservationResult> ReleaseTreeAsync(IReadOnlyList<long> objectIds,
        TreeKind kind,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<TreeObservationResult>(TreeWorkflowMethods.Release,
            new TreeReleaseRequest(objectIds, kind),
            cancellationToken);

    public Task<ElementSelectorResult> GetElementSelectorAsync(long objectId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ElementSelectorResult>(TreeWorkflowMethods.Selector,
            new ObjectRequest(objectId),
            cancellationToken);

    public Task<ElementMetadata>
        GetElementMetadataAsync(long objectId, CancellationToken cancellationToken = default) =>
        InvokeAsync<ElementMetadata>(TreeWorkflowMethods.Metadata, new ObjectRequest(objectId), cancellationToken);
}
