using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class TreeClient
{
    private readonly DiagnosticsRpcContext context;

    internal TreeClient(DiagnosticsRpcContext context)
    {
        this.context = context;
        context.Subscribe((method, payload) =>
        {
            switch (method)
            {
                case Methods.TreeInvalidated:
                    TreeInvalidated?.Invoke(ProtocolJson.Read<TreeInvalidatedNotification>(payload));
                    return true;
                default:
                    return false;
            }
        });
    }

    public event Action<TreeInvalidatedNotification>? TreeInvalidated;

    public Task<IReadOnlyList<TreeNodeInfo>>
        GetRootsAsync(TreeKind kind, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<TreeNodeInfo>>(Methods.TreeRoots,
            new TreeRootsRequest(kind),
            cancellationToken);

    public Task<IReadOnlyList<TreeNodeInfo>> GetChildrenAsync(long objectId,
        TreeKind kind,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<TreeNodeInfo>>(Methods.TreeChildren,
            new TreeChildrenRequest(objectId, kind),
            cancellationToken);

    public Task<ControlTreeSnapshot> GetOfficialTreeAsync(ControlTreeRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ControlTreeSnapshot>(ControlTreeMethods.Snapshot, request, cancellationToken);

    public Task<IReadOnlyList<TreeNodeInfo>>
        GetOfficialAncestorsAsync(long objectId, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<TreeNodeInfo>>(ControlTreeMethods.Ancestors,
            new ObjectRequest(objectId),
            cancellationToken);

    public Task<TreeSearchResult> SearchOfficialTreeAsync(ControlSearchRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<TreeSearchResult>(ControlTreeMethods.Search, request, cancellationToken);

    public Task<IReadOnlyList<TreeNodeInfo>> GetAncestorsAsync(long objectId,
        TreeKind kind,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<TreeNodeInfo>>(ExplorationMethods.Ancestors,
            new TreeAncestorsRequest(objectId, kind),
            cancellationToken);

    public Task<TreeSearchResult> SearchTreeAsync(TreeSearchRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<TreeSearchResult>(ExplorationMethods.Search, request, cancellationToken);

    public Task<TreeChildrenPage> GetChildrenPageAsync(long objectId,
        TreeKind kind,
        int offset = 0,
        int count = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default) => context.InvokeAsync<TreeChildrenPage>(
        TreePagingMethods.Children,
        new TreeChildrenPageRequest(objectId, kind, offset, count, cursor),
        cancellationToken);

    public Task<TemplateParentSelectorResult>
        GetTemplateParentSelectorAsync(long objectId, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<TemplateParentSelectorResult>(TreePagingMethods.TemplateParentSelector,
            new ObjectRequest(objectId),
            cancellationToken);

    public Task<TreeObservationResult>
        ObserveTreeAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<TreeObservationResult>(TreeWorkflowMethods.Observe,
            new TreeObserveRequest(objectId, kind),
            cancellationToken);

    public Task<TreeObservationResult> ReleaseTreeAsync(IReadOnlyList<long> objectIds,
        TreeKind kind,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<TreeObservationResult>(TreeWorkflowMethods.Release,
            new TreeReleaseRequest(objectIds, kind),
            cancellationToken);

    public Task<ElementSelectorResult>
        GetElementSelectorAsync(long objectId, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ElementSelectorResult>(TreeWorkflowMethods.Selector,
            new ObjectRequest(objectId),
            cancellationToken);

    public Task<ElementMetadata>
        GetElementMetadataAsync(long objectId, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ElementMetadata>(TreeWorkflowMethods.Metadata,
            new ObjectRequest(objectId),
            cancellationToken);
}
