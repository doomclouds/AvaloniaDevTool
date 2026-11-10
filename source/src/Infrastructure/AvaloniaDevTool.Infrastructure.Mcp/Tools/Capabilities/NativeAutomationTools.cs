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
public sealed class NativeAutomationTools(ToolSession session, ManagedToolContext context)
    : InteractionToolBase(session, context)
{
    [McpServerTool(Name = "native-windows", ReadOnly = true, Destructive = false),
     Description(
         "Lists real Windows top-level and owned modal windows of the attached local application. Native ids are strings and cannot be used as Avalonia node ids.")]
    public Task<NativeWindowInfo[]> NativeWindows(CancellationToken cancellationToken = default) =>
        WithNativeAsync(cancellationToken, (native, token) => native.GetWindowsAsync(token));

    [McpServerTool(Name = "native-tree", ReadOnly = true, Destructive = false),
     Description(
         "Reads a bounded Windows UI Automation tree inside the attached application's windows, including system file pickers.")]
    public Task<NativeTreeResult> NativeTree(string? rootNodeId = null,
        int maxDepth = 6,
        int maxNodes = 500,
        CancellationToken cancellationToken = default) =>
        WithNativeAsync(cancellationToken,
            (native, token) =>
                native.GetTreeAsync(new() { RootNodeId = rootNodeId, MaxDepth = maxDepth, MaxNodes = maxNodes },
                    token));

    [McpServerTool(Name = "native-find", ReadOnly = true, Destructive = false),
     Description(
         "Finds native elements by UI Automation properties within the attached application's scope. Inspect truncation before assuming a complete result.")]
    public Task<NativeFindResult> NativeFind(string? rootNodeId = null,
        string? automationId = null,
        string? name = null,
        string? className = null,
        string? controlType = null,
        int maxDepth = 15,
        int maxNodes = 2000,
        int maxResults = 50,
        CancellationToken cancellationToken = default) =>
        WithNativeAsync(cancellationToken,
            (native, token) => native.FindAsync(new()
                {
                    RootNodeId = rootNodeId, AutomationId = automationId,
                    Name = name, ClassName = className, ControlType = controlType, MaxDepth = maxDepth,
                    MaxNodes = maxNodes,
                    MaxResults = maxResults
                },
                token));

    [McpServerTool(Name = "native-inspect", ReadOnly = true, Destructive = false),
     Description(
         "Reads a native element's current properties and supported automation patterns. Expired ids fail instead of targeting a replacement window.")]
    public Task<NativeElementInfo> NativeInspect(string nodeId, CancellationToken cancellationToken = default) =>
        WithNativeAsync(cancellationToken, (native, token) => native.GetElementAsync(nodeId, token));

    [McpServerTool(Name = "native-action", ReadOnly = false, Destructive = true),
     Description(
         "Uses a native UI Automation pattern: Invoke, SetValue, Select, AddToSelection, RemoveFromSelection, Expand, Collapse, Toggle, Focus, ScrollIntoView, Scroll, Close, Minimize, Maximize or Restore. Modal actions may return pending; inspect the UI or query their operation id before claiming completion.")]
    public Task<NativeActionResult> NativeAction(string nodeId,
        string action,
        string? value = null,
        string horizontalScroll = "NoAmount",
        string verticalScroll = "NoAmount",
        CancellationToken cancellationToken = default) =>
        WithNativeAsync(cancellationToken,
            (native, token) => native.ExecuteAsync(new()
                {
                    NodeId = nodeId, Action = action,
                    Value = value, HorizontalScroll = horizontalScroll, VerticalScroll = verticalScroll
                },
                token));

    [McpServerTool(Name = "native-action-status", ReadOnly = true, Destructive = false),
     Description("Reads the completion or failure of a native action previously reported as pending.")]
    public Task<NativeActionResult> NativeActionStatus(string operationId,
        CancellationToken cancellationToken = default) =>
        WithNativeAsync(cancellationToken, (native, token) => native.GetActionStatusAsync(operationId, token));

    [McpServerTool(Name = "native-input", ReadOnly = false, Destructive = true),
     Description(
         "Injects real Windows input into a verified element of the attached local application. kind: Click, DoubleClick, RightClick, Move, Wheel, Text, KeyChord or Drag. Coordinates are node-local; drag requires a targetNodeId in the same native root. The worker validates focus/ownership and pairs releases. Injected does not verify the application's business effect or IME composition.")]
    public Task<NativeInputResult> NativeInput(string nodeId,
        string kind,
        double? x = null,
        double? y = null,
        string? text = null,
        string[]? keys = null,
        string[]? modifiers = null,
        string? targetNodeId = null,
        double? targetX = null,
        double? targetY = null,
        int wheelDelta = 120,
        int dragSteps = 12,
        CancellationToken cancellationToken = default) =>
        WithNativeAsync(cancellationToken,
            (native, token) => native.SendInputAsync(new()
                {
                    NodeId = nodeId, Kind = kind, X = x, Y = y,
                    Text = text, Keys = keys ?? [], Modifiers = modifiers ?? [], TargetNodeId = targetNodeId,
                    TargetX = targetX,
                    TargetY = targetY, WheelDelta = wheelDelta, DragSteps = dragSteps
                },
                token));
}
