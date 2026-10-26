using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "object", ReadOnly = true),
     Description(
         "Read public CLR properties and fields, or bounded IList/IDictionary items. Complex values have weak object IDs for explicit drill-in; getters run on the target UI thread and per-member failures are returned.")]
    public async Task<object> InspectObject(long objectId,
        bool items = false,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        return items
            ? await client.GetRuntimeItemsAsync(objectId, offset, limit, cancellationToken)
            : await client.GetRuntimeMembersAsync(objectId, offset, limit, cancellationToken);
    }

    [McpServerTool(Name = "layout", ReadOnly = true),
     Description(
         "Capture bounded VisualGeometry layers with real local-to-root matrices for a 3D hierarchy view. This captures geometry, visibility and clip declarations, not per-layer compositor pixels.")]
    public async Task<LayoutSnapshot> Layout(long nodeId,
        int maxNodes = 128,
        int maxDepth = 12,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).GetLayoutSnapshotAsync(nodeId,
            maxNodes,
            maxDepth,
            cancellationToken);
}
