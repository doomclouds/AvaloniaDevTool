using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<CompositionSnapshot> GetCompositionSnapshotAsync(CompositionSnapshotRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<CompositionSnapshot>(CompositionMethods.Snapshot, request, cancellationToken);

    public Task<CompositionSnapshot> GetCompositionSnapshotAsync(long objectId,
        CancellationToken cancellationToken = default) =>
        GetCompositionSnapshotAsync(new CompositionSnapshotRequest(objectId), cancellationToken);
}
