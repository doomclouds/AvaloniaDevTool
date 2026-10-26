using System.ComponentModel;
using AvaloniaDevTool.NativeAutomation;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class AgentTools
{
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
