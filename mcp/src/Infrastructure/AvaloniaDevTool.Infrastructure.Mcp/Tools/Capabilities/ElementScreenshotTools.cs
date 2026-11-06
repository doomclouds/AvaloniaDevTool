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
public sealed class ElementScreenshotTools(ToolSession session, ManagedToolContext context)
    : ManagedToolBase(session, context)
{
    [McpServerTool(Name = "screenshot", ReadOnly = true, Destructive = false),
     Description("Captures a PNG screenshot of a UI element.")]
    public Task<ImageContentBlock> Screenshot(long nodeId, CancellationToken cancellationToken = default) =>
        WithClientAsync(cancellationToken,
            async (client, token) =>
            {
                var capture = await client.Rendering.CaptureElementAsync(NativeNodeId(nodeId), token)
                    .ConfigureAwait(false);
                return ImageContentBlock.FromBytes(Convert.FromBase64String(capture.PngBase64), "image/png");
            });
}
