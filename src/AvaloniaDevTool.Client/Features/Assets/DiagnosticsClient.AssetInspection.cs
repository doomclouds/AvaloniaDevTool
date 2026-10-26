using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<IReadOnlyList<AssetInfo>> GetAssetsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<AssetInfo>>(InspectionMethods.Assets, null, cancellationToken);

    public Task<AssetPreview> OpenAssetAsync(string uri, CancellationToken cancellationToken = default) =>
        InvokeAsync<AssetPreview>(InspectionMethods.OpenAsset, new OpenAssetRequest(uri), cancellationToken);
}
