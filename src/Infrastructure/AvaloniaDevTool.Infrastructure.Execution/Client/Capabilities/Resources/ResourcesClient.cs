using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class ResourcesClient
{
    private readonly DiagnosticsRpcContext context;

    internal ResourcesClient(DiagnosticsRpcContext context)
    {
        this.context = context;
    }

    public Task<ResourceTreeSnapshot> QueryResourceTreeAsync(ResourceTreeQueryRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ResourceTreeSnapshot>(ResourceQueryMethods.Trees, request, cancellationToken);

    public Task<ResourceEntriesSnapshot> QueryResourceEntriesAsync(ResourceEntriesQueryRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ResourceEntriesSnapshot>(ResourceQueryMethods.Entries, request, cancellationToken);

    public Task<ResourceQueryEntryInfo> ReadResourceEntryAsync(ResourceReadEntryRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ResourceQueryEntryInfo>(ResourceQueryMethods.ReadEntry, request, cancellationToken);

    public Task<IReadOnlyList<ResourceDictionaryInfo>> GetResourcesAsync(long? objectId = null,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<ResourceDictionaryInfo>>(InspectionMethods.Resources,
            new ResourcesRequest(objectId),
            cancellationToken);

    public Task<IReadOnlyList<ResourceEntryInfo>>
        GetResourceEntriesAsync(long dictionaryId, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<ResourceEntryInfo>>(InspectionMethods.ResourceEntries,
            new ResourceEntriesRequest(dictionaryId),
            cancellationToken);

    public async Task
        SetResourceAsync(long dictionaryId, string key, string value, CancellationToken cancellationToken = default) =>
        _ = await context.InvokeAsync<OperationResult>(InspectionMethods.SetResource,
            new SetResourceRequest(dictionaryId, key, value),
            cancellationToken).ConfigureAwait(false);

    public async Task
        ResetResourceAsync(long dictionaryId, string key, CancellationToken cancellationToken = default) => _ =
        await context.InvokeAsync<OperationResult>(InspectionMethods.ResetResource,
            new ResetResourceRequest(dictionaryId, key),
            cancellationToken).ConfigureAwait(false);
}
