using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public event Action<ActivityEntry>? ActivityEntryAdded;
    public event Action<ActivityStatus>? ActivityStateChanged;
    public Task<StyleSnapshot> GetStylesAsync(long objectId, CancellationToken cancellationToken = default) =>
        InvokeAsync<StyleSnapshot>(InspectionMethods.Styles, new ObjectRequest(objectId), cancellationToken);
    public Task<IReadOnlyList<ResourceDictionaryInfo>> GetResourcesAsync(long? objectId = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<ResourceDictionaryInfo>>(InspectionMethods.Resources, new ResourcesRequest(objectId), cancellationToken);
    public Task<IReadOnlyList<ResourceEntryInfo>> GetResourceEntriesAsync(long dictionaryId, CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<ResourceEntryInfo>>(InspectionMethods.ResourceEntries, new ResourceEntriesRequest(dictionaryId), cancellationToken);
    public async Task SetResourceAsync(long dictionaryId, string key, string value, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(InspectionMethods.SetResource, new SetResourceRequest(dictionaryId, key, value), cancellationToken).ConfigureAwait(false);
    public async Task ResetResourceAsync(long dictionaryId, string key, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(InspectionMethods.ResetResource, new ResetResourceRequest(dictionaryId, key), cancellationToken).ConfigureAwait(false);
    public Task<IReadOnlyList<AssetInfo>> GetAssetsAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<AssetInfo>>(InspectionMethods.Assets, null, cancellationToken);
    public Task<AssetPreview> OpenAssetAsync(string uri, CancellationToken cancellationToken = default) =>
        InvokeAsync<AssetPreview>(InspectionMethods.OpenAsset, new OpenAssetRequest(uri), cancellationToken);
    public async Task StartActivityAsync(ActivityStartRequest request, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(ActivityMethods.Start, request, cancellationToken).ConfigureAwait(false);
    public async Task StopActivityAsync(CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(ActivityMethods.Stop, null, cancellationToken).ConfigureAwait(false);
    public async Task ClearActivityAsync(CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(ActivityMethods.Clear, null, cancellationToken).ConfigureAwait(false);
    public Task<IReadOnlyList<ActivityEntry>> GetActivityAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<ActivityEntry>>(ActivityMethods.Snapshot, null, cancellationToken);
    public Task<ActivityStatus> GetActivityStatusAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<ActivityStatus>(ActivityMethods.Status, null, cancellationToken);
}

