using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class LayoutRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, LayoutInspectionService layout)
    {
        routes.Map<LayoutSnapshotRequest>(LayoutMethods.Snapshot, request => layout.Snapshot(request));
    }
}
