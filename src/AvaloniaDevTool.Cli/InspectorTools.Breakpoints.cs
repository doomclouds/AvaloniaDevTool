using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "breakpoints", ReadOnly = true), Description("List session property/event breakpoints, or read their bounded hit history when history=true.")]
    public async Task<object> Breakpoints(bool history = false, CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        return history ? await client.GetBreakpointHistoryAsync(cancellationToken) : await client.GetBreakpointsAsync(cancellationToken);
    }

    [McpServerTool(Name = "breakpoint-create"), Description("Observe a property or routed event. memberId is owner-qualified, e.g. Avalonia.Controls.TextBlock:Text or Avalonia.Controls.Button:Click. Event objectId filters route receivers; sourceObjectId independently filters the actual Source. Either null is unrestricted; both filters combine. Suspend requires an attached debugger.")]
    public async Task<BreakpointInfo> CreateBreakpoint(BreakpointKind kind, string memberId, long? objectId = null,
        BreakpointComparison comparison = BreakpointComparison.Always, long compareTo = 1, bool logMessage = true,
        bool removeOnHit = false, bool suspend = false, CancellationToken cancellationToken = default, long? sourceObjectId = null) =>
        await (await session.GetAsync(cancellationToken)).CreateBreakpointAsync(new BreakpointSpec(kind, memberId, objectId,
            Comparison: comparison, CompareTo: compareTo, LogMessage: logMessage, RemoveOnHit: removeOnHit, Suspend: suspend,
            SourceObjectId: sourceObjectId), cancellationToken);

    [McpServerTool(Name = "breakpoint-update"), Description("Replace a breakpoint definition, optionally resetting its hit count. Disabling and removing release observation handlers.")]
    public async Task<BreakpointInfo> UpdateBreakpoint(long id, BreakpointSpec definition, bool resetHitCount = false,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).UpdateBreakpointAsync(id, definition, resetHitCount, cancellationToken);

    [McpServerTool(Name = "breakpoint-remove"), Description("Remove a breakpoint and release its property/event observation.")]
    public async Task<string> RemoveBreakpoint(long id, CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).RemoveBreakpointAsync(id, cancellationToken);
        return "Breakpoint removed";
    }
}

