using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class RecordingRegionRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, ElementRecordingRegionService regions) =>
        routes.Map<ElementRecordingRegionRequest>(RecordingRegionMethods.Resolve, regions.Resolve);
}
