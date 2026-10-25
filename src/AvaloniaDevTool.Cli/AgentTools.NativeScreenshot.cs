using System.ComponentModel;
using System.Text.Json;
using AvaloniaDevTool.NativeAutomation;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class AgentTools
{
    [McpServerTool(Name = "native-screenshot", ReadOnly = true, Destructive = false), Description("Captures a PNG of a native node in the attached application's scoped window, without activating it. Optional x/y/width/height must all be supplied in node-local physical pixels. Uses the window's PrintWindow surface, never an arbitrary desktop region; GPU/protected surfaces may be incomplete. Returns image plus bounds, method and limitations.")]
    public Task<CallToolResult> NativeScreenshot(string nodeId, double? x = null, double? y = null,
        double? width = null, double? height = null, int maxPixels = 8_000_000,
        CancellationToken cancellationToken = default) => WithNativeAsync(cancellationToken, async (native, token) =>
    {
        var capture = await native.CaptureAsync(new NativeScreenshotRequest
        { NodeId = nodeId, X = x, Y = y, Width = width, Height = height, MaxPixels = maxPixels }, token).ConfigureAwait(false);
        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = JsonSerializer.Serialize(new
                {
                    capture.NodeId, capture.Width, capture.Height, capture.ScreenBounds, capture.CaptureMethod,
                    capture.Limitations, CoordinateSpace = "physical-pixels", MaximumEncodedBytes = 16 * 1024 * 1024
                }) },
                ImageContentBlock.FromBytes(Convert.FromBase64String(capture.PngBase64), "image/png")
            ]
        };
    });
}
