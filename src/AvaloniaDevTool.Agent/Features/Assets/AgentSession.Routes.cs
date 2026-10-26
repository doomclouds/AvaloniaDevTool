using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterAssetsMethods()
    {
        Map(InspectionMethods.Assets, () => AssetInspectionService.List());
        Map<OpenAssetRequest>(InspectionMethods.OpenAsset, request => AssetInspectionService.Open(request.Uri));
        Map<BeginAssetExportRequest>(AssetExportMethods.Begin, request => _assetExports.Begin(request));
        Map<ReadAssetExportRequest>(AssetExportMethods.Read, request => _assetExports.Read(request));
        Map<CloseAssetExportRequest>(AssetExportMethods.Close, request => _assetExports.Close(request));
    }
}
