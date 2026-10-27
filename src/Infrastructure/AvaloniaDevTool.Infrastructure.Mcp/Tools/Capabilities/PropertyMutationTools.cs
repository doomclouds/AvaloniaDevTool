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
public sealed class PropertyMutationTools(ToolSession session) : TargetToolBase(session)
{
    [McpServerTool(Name = "follow-focus"),
     Description(
         "Follow keyboard focus in the target application through selection notifications. Disabling releases the mode for this session.")]
    public async Task<string> FollowFocus(bool enabled, CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).Selection.SetFollowFocusAsync(enabled, cancellationToken);
        return enabled ? "Focus following enabled" : "Focus following disabled";
    }

    [McpServerTool(Name = "set-brush"),
     Description(
         "Apply a structured Solid, Linear, Radial or Conic brush draft as a reversible diagnostic override. Read current fields and allowed kinds from props presentation.brush; clear with set-prop JSON null or detach. Does not mutate the application's original brush or its gradient stops.")]
    public async Task<OperationResult> SetBrush(long nodeId,
        string propertyName,
        BrushDto brush,
        CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).Properties.SetBrushAsync(
            new SetBrushRequest(nodeId, propertyName, brush),
            cancellationToken);
        return new OperationResult();
    }
}
