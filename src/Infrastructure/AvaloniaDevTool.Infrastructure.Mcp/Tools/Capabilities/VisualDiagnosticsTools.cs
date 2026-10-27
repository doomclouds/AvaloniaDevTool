using AvaloniaDevTool.Client;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace AvaloniaDevTool.Cli;

[McpServerToolType]
public sealed class VisualDiagnosticsTools(ToolSession session) : TargetToolBase(session)
{
    [McpServerTool(Name = "renderer-diagnostics", ReadOnly = true),
     Description(
         "Read actual renderer overlay flags, supported values, window IDs, tool ownership and application-change conflicts.")]
    public async Task<RendererDiagnosticsStatusDto>
        RendererDiagnostics(CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).Rendering.GetRendererDiagnosticsAsync(cancellationToken);

    [McpServerTool(Name = "renderer-diagnostics-set"),
     Description(
         "Apply additive native Avalonia renderer overlays: Fps, DirtyRects, LayoutTimeGraph and RenderTimeGraph. Null windowId targets all current roots; followNewWindows is valid only with null windowId. Existing application flags are preserved; later application changes relinquish tool ownership and report a conflict.")]
    public async Task<RendererDiagnosticsStatusDto> SetRendererDiagnostics(RendererOverlayFlags flags,
        long? windowId = null,
        bool followNewWindows = false,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).Rendering.SetRendererDiagnosticsAsync(
            new RendererDiagnosticsSetRequest(windowId, flags, followNewWindows),
            cancellationToken);

    [McpServerTool(Name = "renderer-diagnostics-stop"),
     Description(
         "Stop tracking future windows and restore renderer overlay values still owned by this session, preserving later application changes and reporting conflicts.")]
    public async Task<RendererDiagnosticsStatusDto> StopRendererDiagnostics(CancellationToken cancellationToken =
        default) =>
        await (await session.GetAsync(cancellationToken)).Rendering.StopRendererDiagnosticsAsync(cancellationToken);

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
        await (await session.GetAsync(cancellationToken)).Rendering.GetCompositionSnapshotAsync(
            new CompositionSnapshotRequest(nodeId,
                maxNodes,
                maxDepth,
                maxImageBytes,
                maxTotalImageBytes,
                maxTotalPixels,
                scaling),
            cancellationToken);

    [McpServerTool(Name = "overlay", ReadOnly = true),
     Description(
         "Read root-DIP bounds, signed margin/padding regions, ruler/guideline geometry, device scaling and limitations for the current highlighted control or a specified real control.")]
    public async Task<OverlaySnapshot> Overlay(long? nodeId = null, CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).Overlays.GetOverlaySnapshotAsync(nodeId, cancellationToken);

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
        await (await session.GetAsync(cancellationToken)).Overlays.ConfigureOverlayAsync(
            new OverlayOptions(bounds, margin, padding, info, rulers, guidelines),
            nodeId,
            cancellationToken);

    [McpServerTool(Name = "overlay-clear"),
     Description("Remove the current diagnostic overlay while retaining display preferences.")]
    public async Task<OperationResult> ClearOverlay(CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).Selection.HighlightAsync(null, cancellationToken);
        return new OperationResult();
    }

    [McpServerTool(Name = "popup-freeze", ReadOnly = true),
     Description(
         "Read popup freeze scope, current native/overlay popups, ownership and lifetime limitations. Freeze suppresses public Popup light dismissal; application close, ContextMenu interaction-handler dismissal, ToolTipService and transient Flyout move-away remain authoritative.")]
    public async Task<PopupFreezeStatusDto> PopupFreeze(CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).Popups.GetPopupFreezeAsync(cancellationToken);

    [McpServerTool(Name = "popup-freeze-set"),
     Description(
         "Freeze or resume automatic popup light dismissal in one configured root. Null rootId targets all configured roots and future roots. Includes newly opened and nested popups. Application Close/Hide and later application configuration changes are preserved. ContextMenu interaction-handler dismissal, ToolTipService pointer/timer dismissal and transient Flyout move-away are not suppressed.")]
    public async Task<PopupFreezeStatusDto> SetPopupFreeze(bool isFrozen,
        long? rootId = null,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).Popups.SetPopupFreezeAsync(new(isFrozen, rootId),
            cancellationToken);

    [McpServerTool(Name = "popup-freeze-resume"),
     Description(
         "Resume all popup roots and restore only light-dismiss values still owned by this session. Closed popups are never reopened; later application writes are preserved.")]
    public async Task<PopupFreezeStatusDto> ResumePopups(CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).Popups.ResumePopupsAsync(cancellationToken);

    [McpServerTool(Name = "layout", ReadOnly = true),
     Description(
         "Capture bounded VisualGeometry layers with real local-to-root matrices for a 3D hierarchy view. This captures geometry, visibility and clip declarations, not per-layer compositor pixels.")]
    public async Task<LayoutSnapshot> Layout(long nodeId,
        int maxNodes = 128,
        int maxDepth = 12,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).Layout.GetLayoutSnapshotAsync(nodeId,
            maxNodes,
            maxDepth,
            cancellationToken);
}
