using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class AssetsRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, AssetExportService exports)
    {
        routes.Map(InspectionMethods.Assets, () => AssetInspectionService.List());
        routes.Map<OpenAssetRequest>(InspectionMethods.OpenAsset, request => AssetInspectionService.Open(request.Uri));
        routes.Map<BeginAssetExportRequest>(AssetExportMethods.Begin, request => exports.Begin(request));
        routes.Map<ReadAssetExportRequest>(AssetExportMethods.Read, request => exports.Read(request));
        routes.Map<CloseAssetExportRequest>(AssetExportMethods.Close, request => exports.Close(request));
    }
}
