using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<IReadOnlyList<TreeNodeInfo>> GetAncestorsAsync(long objectId,
        TreeKind kind,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<TreeNodeInfo>>(ExplorationMethods.Ancestors,
            new TreeAncestorsRequest(objectId, kind),
            cancellationToken);

    public Task<TreeSearchResult> SearchTreeAsync(TreeSearchRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<TreeSearchResult>(ExplorationMethods.Search, request, cancellationToken);
}
