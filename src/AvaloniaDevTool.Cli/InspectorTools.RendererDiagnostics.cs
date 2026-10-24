using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "renderer-diagnostics", ReadOnly = true),
     Description("Read actual renderer overlay flags, supported values, window IDs, tool ownership and application-change conflicts.")]
    public async Task<RendererDiagnosticsStatusDto> RendererDiagnostics(CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).GetRendererDiagnosticsAsync(cancellationToken);

    [McpServerTool(Name = "renderer-diagnostics-set"),
     Description("Apply additive native Avalonia renderer overlays: Fps, DirtyRects, LayoutTimeGraph and RenderTimeGraph. Null windowId targets all current roots; followNewWindows is valid only with null windowId. Existing application flags are preserved; later application changes relinquish tool ownership and report a conflict.")]
    public async Task<RendererDiagnosticsStatusDto> SetRendererDiagnostics(RendererOverlayFlags flags,
        long? windowId = null, bool followNewWindows = false, CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).SetRendererDiagnosticsAsync(
            new RendererDiagnosticsSetRequest(windowId, flags, followNewWindows), cancellationToken);

    [McpServerTool(Name = "renderer-diagnostics-stop"),
     Description("Stop tracking future windows and restore renderer overlay values still owned by this session, preserving later application changes and reporting conflicts.")]
    public async Task<RendererDiagnosticsStatusDto> StopRendererDiagnostics(CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).StopRendererDiagnosticsAsync(cancellationToken);
}
