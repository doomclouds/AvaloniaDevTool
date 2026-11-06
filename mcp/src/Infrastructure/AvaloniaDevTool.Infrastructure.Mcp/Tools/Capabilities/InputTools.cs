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
public sealed class InputTools(ToolSession session, ManagedToolContext context) : ManagedToolBase(session, context)
{
    [McpServerTool(Name = "input", ReadOnly = false, Destructive = true),
     Description("Sends an input event to a UI element.")]
    public Task<InputResult> Input(long nodeId,
        ControlInputEvent inputEvent,
        string? arg = null,
        CancellationToken cancellationToken = default) => WithClientAsync(cancellationToken,
        (client, token) =>
        {
            if (!Enum.IsDefined(inputEvent))
                throw new McpException("invalid_input: Unknown input event.");
            var kind = inputEvent switch
            {
                ControlInputEvent.Click => InputKind.Click,
                ControlInputEvent.KeyDown => InputKind.KeyDown,
                ControlInputEvent.KeyUp => InputKind.KeyUp,
                ControlInputEvent.Text => InputKind.Text,
                _ => throw new McpException("invalid_input: Unknown input event.")
            };
            return client.Input.SendInputAsync(new AvaloniaDevTool.Protocol.InputRequest(NativeNodeId(nodeId),
                    kind,
                    Text: inputEvent == ControlInputEvent.Text ? arg : null,
                    Key: inputEvent is ControlInputEvent.KeyDown or ControlInputEvent.KeyUp ? arg : null),
                token);
        });
}
