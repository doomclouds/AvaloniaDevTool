using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<OverlaySnapshot> ConfigureOverlayAsync(OverlayOptions options, long? objectId = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<OverlaySnapshot>(OverlayMethods.Configure, new OverlayConfigureRequest(options, objectId), cancellationToken);

    public Task<OverlaySnapshot> GetOverlaySnapshotAsync(long? objectId = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<OverlaySnapshot>(OverlayMethods.Snapshot, new OverlaySnapshotRequest(objectId), cancellationToken);
}
