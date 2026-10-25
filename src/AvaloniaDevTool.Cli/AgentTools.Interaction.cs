using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed record AgentDragWaypoint(long NodeId, double X, double Y);

public sealed partial class AgentTools
{
    [McpServerTool(Name = "ui-input", ReadOnly = false, Destructive = true), Description("Sends framework Text, KeyDown, KeyUp, PointerMove, PointerDown, PointerUp, Wheel or Click to a canonical Avalonia node. Coordinates are local; native OS dialogs require native tools. This does not emulate IME composition.")]
    public Task<InputResult> UiInput(long nodeId, InputKind kind, string? text = null, string? key = null, string[]? modifiers = null,
        double? x = null, double? y = null, double deltaX = 0, double deltaY = 0, string button = "Left", CancellationToken cancellationToken = default) =>
        official.WithAgentClientAsync(cancellationToken, (client, token) => client.SendInputAsync(new(
            official.AgentNativeNodeId(nodeId), kind, text, key, modifiers, x, y, deltaX, deltaY, button), token));

    [McpServerTool(Name = "ui-click", ReadOnly = false, Destructive = true), Description("Delivers one or more framework clicks with the selected mouse button. Supports double-click and context-menu behavior. Read the application's resulting state to verify business effects.")]
    public Task<InputResult> UiClick(long nodeId, int clickCount = 1, string button = "Left", double? x = null, double? y = null,
        string[]? modifiers = null, CancellationToken cancellationToken = default) =>
        official.WithAgentClientAsync(cancellationToken, (client, token) => client.ClickAsync(new(
            official.AgentNativeNodeId(nodeId), clickCount, button, x, y, modifiers), token));

    [McpServerTool(Name = "ui-key-chord", ReadOnly = false, Destructive = true), Description("Sends an ordered key chord with balanced reverse-order releases, including modifier key events. Uses Avalonia key names, for example keys=[A], modifiers=[Control].")]
    public Task<InputResult> UiKeyChord(long nodeId, string[] keys, string[]? modifiers = null, CancellationToken cancellationToken = default) =>
        official.WithAgentClientAsync(cancellationToken, (client, token) => client.SendKeyChordAsync(new(
            official.AgentNativeNodeId(nodeId), keys, modifiers), token));

    [McpServerTool(Name = "ui-drag", ReadOnly = false, Destructive = true), Description("Drags an Avalonia pointer between controls inside one top-level window, preserving capture and root coordinates. Waypoint coordinates are local to each waypoint node. This is pointer dragging, not native file/data DragDrop.")]
    public Task<InputResult> UiDrag(long sourceNodeId, long targetNodeId, double? sourceX = null, double? sourceY = null,
        double? targetX = null, double? targetY = null, AgentDragWaypoint[]? path = null, int steps = 12, string button = "Left",
        string[]? modifiers = null, CancellationToken cancellationToken = default) =>
        official.WithAgentClientAsync(cancellationToken, (client, token) => client.DragAsync(new(
            official.AgentNativeNodeId(sourceNodeId), official.AgentNativeNodeId(targetNodeId), sourceX, sourceY, targetX, targetY,
            steps, path?.Select(point => new DragWaypoint(official.AgentNativeNodeId(point.NodeId), point.X, point.Y)).ToArray(), button, modifiers), token));

    [McpServerTool(Name = "ui-text-selection", ReadOnly = false, Destructive = true), Description("Selects a TextBox range and optionally replaces it using real text input. length omitted selects to the end; replacementText omitted only selects; an empty replacement deletes the selection. Returns actual resulting selection state.")]
    public Task<TextSelectionResult> UiTextSelection(long nodeId, int start = 0, int? length = null, string? replacementText = null,
        CancellationToken cancellationToken = default) =>
        official.WithAgentClientAsync(cancellationToken, (client, token) => client.SelectTextAsync(new(
            official.AgentNativeNodeId(nodeId), start, length, replacementText), token));

    [McpServerTool(Name = "ui-select-item", ReadOnly = false, Destructive = true), Description("Selects a real item index in an Avalonia selecting control, including ComboBox, ListBox and TabControl. Returns the observed selected index and item rather than only an acknowledgement.")]
    public Task<ItemSelectionResult> UiSelectItem(long nodeId, int index, CancellationToken cancellationToken = default) =>
        official.WithAgentClientAsync(cancellationToken, (client, token) => client.SelectItemAsync(new(
            official.AgentNativeNodeId(nodeId), index), token));

    [McpServerTool(Name = "ui-control-action", ReadOnly = false, Destructive = true), Description("Uses a control's actual automation provider: Invoke, Toggle, Expand or Collapse. Returns observed toggle/expand state; unsupported patterns fail explicitly.")]
    public Task<ControlActionResult> UiControlAction(long nodeId, string action, CancellationToken cancellationToken = default)
    {
        if (!Enum.GetNames<ControlAction>().Contains(action, StringComparer.OrdinalIgnoreCase))
            throw new McpException("invalid_argument: action must be Invoke, Toggle, Expand or Collapse.");
        return official.WithAgentClientAsync(cancellationToken, (client, token) => client.PerformControlActionAsync(new(
            official.AgentNativeNodeId(nodeId), Enum.Parse<ControlAction>(action, true)), token));
    }
}
