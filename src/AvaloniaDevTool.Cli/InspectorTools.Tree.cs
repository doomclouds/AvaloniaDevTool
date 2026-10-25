using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "tree", ReadOnly = true), Description("Read Visual, Logical or Merged tree snapshots. includeTemplates=false stops recursion at Merged template groups. depth=0 reads roots, -1 recurses within maxNodes (1..2000) and 64 levels. Optional offset/count/cursor select direct-child paging instead: require nodeId and depth=1, count=1..2000 (default 100); maxNodes and includeTemplates apply only to recursive mode. Page metadata exposes total count, continuation and hasMore. Changed branches reject stale cursors explicitly.")]
    public async Task<ToolTreeSnapshot> Tree(long? nodeId = null, TreeKind kind = TreeKind.Logical, int depth = 1, int maxNodes = 500, bool includeTemplates = true, CancellationToken cancellationToken = default,
        int? offset = null, int? count = null, string? cursor = null)
        => await WithTreeErrorsAsync(() => ReadTreeAsync(nodeId, kind, depth, maxNodes, includeTemplates, cancellationToken, offset, count, cursor));

    private async Task<ToolTreeSnapshot> ReadTreeAsync(long? nodeId, TreeKind kind, int depth, int maxNodes, bool includeTemplates,
        CancellationToken cancellationToken, int? offset, int? count, string? cursor)
    {
        if (depth < -1 || depth > 64 || maxNodes is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(depth), "depth must be -1..64 and maxNodes 1..2000.");
        var client = await session.GetAsync(cancellationToken);
        var roots = nodeId is { } id ? await NodeAsRoot(client, id, kind, cancellationToken) : await client.GetRootsAsync(kind, cancellationToken);
        if (offset is not null || count is not null || cursor is not null)
        {
            if (nodeId is not { } parentId || depth != 1)
                throw new ArgumentException("Direct-child paging requires nodeId and depth=1.");
            var page = await client.GetChildrenPageAsync(parentId, kind, offset ?? 0, count ?? 100, cursor, cancellationToken);
            return new([new(roots.Single(), page.Children.Select(child => new ToolTreeNode(child, [])).ToArray())],
                page.Children.Count + 1, page.HasMore, page);
        }
        var visited = new HashSet<long>();
        var truncated = false;
        async Task<ToolTreeNode?> Visit(TreeNodeInfo node, int remaining, int level)
        {
            if (visited.Count >= maxNodes) { truncated = true; return null; }
            if (!visited.Add(node.Id)) return null;
            var children = new List<ToolTreeNode>();
            if (node.HasChildren && remaining != 0 && (includeTemplates || node.Role != TreeNodeRole.TemplateGroup))
            {
                if (level >= 64) truncated = true;
                else
                {
                    string? continuation = null;
                    do
                    {
                        if (visited.Count >= maxNodes) { truncated = true; break; }
                        var page = await client.GetChildrenPageAsync(node.Id, kind, count: Math.Min(100, maxNodes - visited.Count), cursor: continuation, cancellationToken: cancellationToken);
                        foreach (var child in page.Children)
                        {
                            var result = await Visit(child, remaining < 0 ? -1 : remaining - 1, level + 1);
                            if (result is not null) children.Add(result);
                        }
                        continuation = page.NextCursor;
                    } while (continuation is not null);
                }
            }
            return new ToolTreeNode(node, children);
        }
        var items = new List<ToolTreeNode>();
        foreach (var root in roots)
        {
            var result = await Visit(root, depth, 0);
            if (result is not null) items.Add(result);
        }
        return new ToolTreeSnapshot(items, visited.Count, truncated);
    }

    private static async Task<IReadOnlyList<TreeNodeInfo>> NodeAsRoot(DiagnosticsClient client, long id, TreeKind kind, CancellationToken token)
    {
        var path = await client.GetAncestorsAsync(id, kind, token);
        return [path.Last()];
    }

    [McpServerTool(Name = "ancestors", ReadOnly = true), Description("Return the root-to-node path in the selected tree, including the target node.")]
    public async Task<IReadOnlyList<TreeNodeInfo>> Ancestors(long nodeId, TreeKind kind = TreeKind.Logical, CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).GetAncestorsAsync(nodeId, kind, cancellationToken);

    [McpServerTool(Name = "search", ReadOnly = true), Description("Search node type, name and preview text with explicit result and traversal bounds.")]
    public async Task<TreeSearchResult> Search(string query, TreeKind kind = TreeKind.Logical, long? rootId = null, int maxResults = 100,
        int maxVisited = 3000, CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).SearchTreeAsync(new TreeSearchRequest(query, kind, rootId, maxResults, maxVisited), cancellationToken);
}

public sealed record ToolTreeNode(TreeNodeInfo Node, IReadOnlyList<ToolTreeNode> Children);
public sealed record ToolTreeSnapshot(IReadOnlyList<ToolTreeNode> Roots, int Visited, bool Truncated, TreeChildrenPage? Page = null);
