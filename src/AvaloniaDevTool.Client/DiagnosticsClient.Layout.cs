using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<LayoutSnapshot> GetLayoutSnapshotAsync(long objectId, int maxNodes = 128, int maxDepth = 12,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<LayoutSnapshot>(LayoutMethods.Snapshot,
            new LayoutSnapshotRequest(objectId, maxNodes, maxDepth), cancellationToken);
}

