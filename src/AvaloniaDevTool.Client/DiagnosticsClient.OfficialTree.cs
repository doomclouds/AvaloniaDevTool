using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<OfficialTreeSnapshot> GetOfficialTreeAsync(OfficialTreeRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<OfficialTreeSnapshot>(OfficialTreeMethods.Snapshot, request, cancellationToken);

    public Task<IReadOnlyList<TreeNodeInfo>> GetOfficialAncestorsAsync(long objectId, CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<TreeNodeInfo>>(OfficialTreeMethods.Ancestors, new ObjectRequest(objectId), cancellationToken);

    public Task<TreeSearchResult> SearchOfficialTreeAsync(OfficialTreeSearchRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<TreeSearchResult>(OfficialTreeMethods.Search, request, cancellationToken);
}
