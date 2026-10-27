using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class OverlaysRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, OverlayService overlay)
    {
        routes.Map<OverlayConfigureRequest>(OverlayMethods.Configure, request => overlay.Configure(request));
        routes.Map<OverlaySnapshotRequest>(OverlayMethods.Snapshot, request => overlay.Snapshot(request));
    }
}
