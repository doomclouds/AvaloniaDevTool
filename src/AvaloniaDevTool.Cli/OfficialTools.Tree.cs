using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class OfficialTools
{
    [McpServerTool(Name = "tree", ReadOnly = true), Description("Gets child elements. Omit nodeId or use 0 for roots. Depth 1 returns one level; -1 returns the full subtree. Results are flat entries with parentId and depth.")]
    public Task<OfficialTreeSnapshot> Tree(long nodeId = 0, TreeKind treeKind = TreeKind.Logical, int depth = 10,
        CancellationToken cancellationToken = default) => WithClientAsync(cancellationToken, async (client, token) =>
        {
            var snapshot = await client.GetOfficialTreeAsync(new OfficialTreeRequest(NativeNodeId(nodeId), treeKind, depth), token).ConfigureAwait(false);
            return snapshot with
            {
                Entries = snapshot.Entries.Select(entry => entry with
                { Node = PublicNode(entry.Node), ParentId = PublicNodeId(entry.ParentId) }).ToArray()
            };
        });

    [McpServerTool(Name = "ancestors", ReadOnly = true), Description("Gets the parent chain. This implementation returns a root-to-node merged path, including the target.")]
    public Task<IReadOnlyList<TreeNodeInfo>> Ancestors(long nodeId, CancellationToken cancellationToken = default) =>
        WithClientAsync(cancellationToken, async (client, token) =>
            PublicNodes(await client.GetOfficialAncestorsAsync(NativeNodeId(nodeId), token).ConfigureAwait(false)));

    [McpServerTool(Name = "search", ReadOnly = true), Description("Finds elements by type or x:Name (partial match). Omit scopeNodeId or use 0 for all roots. searchTemplates includes template elements. limit=-1 returns all matches.")]
    public Task<TreeSearchResult> Search(string query, long scopeNodeId = 0, bool searchTemplates = false, int limit = -1,
        CancellationToken cancellationToken = default) => WithClientAsync(cancellationToken, async (client, token) =>
        {
            var result = await client.SearchOfficialTreeAsync(new OfficialTreeSearchRequest(query, NativeNodeId(scopeNodeId), searchTemplates, limit), token).ConfigureAwait(false);
            return result with { Matches = PublicNodes(result.Matches) };
        });
}
