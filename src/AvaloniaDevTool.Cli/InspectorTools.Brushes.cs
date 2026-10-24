using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "set-brush"), Description("Apply a structured Solid, Linear, Radial or Conic brush draft as a reversible diagnostic override. Read current fields and allowed kinds from props presentation.brush; clear with set-prop JSON null or detach. Does not mutate the application's original brush or its gradient stops.")]
    public async Task<OperationResult> SetBrush(long nodeId, string propertyName, BrushDto brush, CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).SetBrushAsync(new SetBrushRequest(nodeId, propertyName, brush), cancellationToken);
        return new OperationResult();
    }
}
