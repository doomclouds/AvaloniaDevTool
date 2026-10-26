using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterOverlaysMethods()
    {
        Map<OverlayConfigureRequest>(OverlayMethods.Configure, request => _overlay.Configure(request));
        Map<OverlaySnapshotRequest>(OverlayMethods.Snapshot, request => _overlay.Snapshot(request));
    }
}
