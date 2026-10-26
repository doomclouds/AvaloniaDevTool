using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "composition", ReadOnly = true),
     Description(
         "Capture each real compositor visual's own transparent PNG drawing, hierarchy, transforms and compositing metadata. Images exclude child drawings. Unsupported effects, masks, caches and clips remain explicit limitations.")]
    public async Task<CompositionSnapshot> Composition(long nodeId,
        int maxNodes = 128,
        int maxDepth = 12,
        int maxImageBytes = 262144,
        int maxTotalImageBytes = 2097152,
        long maxTotalPixels = 2097152,
        double scaling = 1,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).GetCompositionSnapshotAsync(
            new CompositionSnapshotRequest(nodeId,
                maxNodes,
                maxDepth,
                maxImageBytes,
                maxTotalImageBytes,
                maxTotalPixels,
                scaling),
            cancellationToken);
}
