using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<ResourceTreeSnapshot> QueryResourceTreeAsync(ResourceTreeQueryRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ResourceTreeSnapshot>(ResourceQueryMethods.Trees, request, cancellationToken);

    public Task<ResourceEntriesSnapshot> QueryResourceEntriesAsync(ResourceEntriesQueryRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ResourceEntriesSnapshot>(ResourceQueryMethods.Entries, request, cancellationToken);

    public Task<ResourceQueryEntryInfo> ReadResourceEntryAsync(ResourceReadEntryRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ResourceQueryEntryInfo>(ResourceQueryMethods.ReadEntry, request, cancellationToken);
}
