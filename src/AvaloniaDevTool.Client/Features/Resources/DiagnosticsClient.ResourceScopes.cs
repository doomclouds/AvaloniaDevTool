using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<IReadOnlyList<ResourceDictionaryInfo>> GetResourcesAsync(long? objectId = null,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<ResourceDictionaryInfo>>(InspectionMethods.Resources,
            new ResourcesRequest(objectId),
            cancellationToken);

    public Task<IReadOnlyList<ResourceEntryInfo>> GetResourceEntriesAsync(long dictionaryId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<ResourceEntryInfo>>(InspectionMethods.ResourceEntries,
            new ResourceEntriesRequest(dictionaryId),
            cancellationToken);

    public async Task SetResourceAsync(long dictionaryId,
        string key,
        string value,
        CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(InspectionMethods.SetResource,
            new SetResourceRequest(dictionaryId, key, value),
            cancellationToken).ConfigureAwait(false);

    public async Task ResetResourceAsync(long dictionaryId,
        string key,
        CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(InspectionMethods.ResetResource,
            new ResetResourceRequest(dictionaryId, key),
            cancellationToken).ConfigureAwait(false);
}
