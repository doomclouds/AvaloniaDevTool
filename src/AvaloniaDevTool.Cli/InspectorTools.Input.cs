using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "input"), Description("Deliver framework-routed text, key, pointer or wheel input to one attached control. Coordinates are local to the target; pointer pairing belongs to this session. This does not inject OS/IME input.")]
    public async Task<InputResult> Input(long nodeId, InputKind kind, string? text = null, string? key = null, string[]? modifiers = null,
        double? x = null, double? y = null, double deltaX = 0, double deltaY = 0, string button = "Left", CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).SendInputAsync(new InputRequest(nodeId, kind, text, key, modifiers, x, y, deltaX, deltaY, button), cancellationToken);
}

