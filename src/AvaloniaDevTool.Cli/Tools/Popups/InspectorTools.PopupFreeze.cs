using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "popup-freeze", ReadOnly = true),
     Description(
         "Read popup freeze scope, current native/overlay popups, ownership and lifetime limitations. Freeze suppresses public Popup light dismissal; application close, ContextMenu interaction-handler dismissal, ToolTipService and transient Flyout move-away remain authoritative.")]
    public async Task<PopupFreezeStatusDto> PopupFreeze(CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).GetPopupFreezeAsync(cancellationToken);

    [McpServerTool(Name = "popup-freeze-set"),
     Description(
         "Freeze or resume automatic popup light dismissal in one configured root. Null rootId targets all configured roots and future roots. Includes newly opened and nested popups. Application Close/Hide and later application configuration changes are preserved. ContextMenu interaction-handler dismissal, ToolTipService pointer/timer dismissal and transient Flyout move-away are not suppressed.")]
    public async Task<PopupFreezeStatusDto> SetPopupFreeze(bool isFrozen,
        long? rootId = null,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).SetPopupFreezeAsync(new(isFrozen, rootId), cancellationToken);

    [McpServerTool(Name = "popup-freeze-resume"),
     Description(
         "Resume all popup roots and restore only light-dismiss values still owned by this session. Closed popups are never reopened; later application writes are preserved.")]
    public async Task<PopupFreezeStatusDto> ResumePopups(CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).ResumePopupsAsync(cancellationToken);
}
