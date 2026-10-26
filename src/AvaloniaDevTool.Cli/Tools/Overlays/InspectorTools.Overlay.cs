using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "overlay", ReadOnly = true),
     Description(
         "Read root-DIP bounds, signed margin/padding regions, ruler/guideline geometry, device scaling and limitations for the current highlighted control or a specified real control.")]
    public async Task<OverlaySnapshot> Overlay(long? nodeId = null, CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).GetOverlaySnapshotAsync(nodeId, cancellationToken);

    [McpServerTool(Name = "overlay-set"),
     Description(
         "Configure the non-interactive application overlay. Supplying nodeId shows that real control; otherwise updates the current overlay's options. Does not change application layout properties.")]
    public async Task<OverlaySnapshot> SetOverlay(long? nodeId = null,
        bool bounds = true,
        bool margin = false,
        bool padding = false,
        bool info = false,
        bool rulers = false,
        bool guidelines = false,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).ConfigureOverlayAsync(
            new OverlayOptions(bounds, margin, padding, info, rulers, guidelines),
            nodeId,
            cancellationToken);

    [McpServerTool(Name = "overlay-clear"),
     Description("Remove the current diagnostic overlay while retaining display preferences.")]
    public async Task<OperationResult> ClearOverlay(CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).HighlightAsync(null, cancellationToken);
        return new OperationResult();
    }
}
