using System.ComponentModel;
using AvaloniaDevTool.NativeAutomation;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class AgentTools
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
}
