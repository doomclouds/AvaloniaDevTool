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
public sealed class VisualCaptureTools(ToolSession session, ManagedToolContext context)
    : InteractionToolBase(session, context)
{
    [McpServerTool(Name = "ui-screenshot", ReadOnly = true, Destructive = false),
     Description(
         "Captures actual PNG pixels from a canonical Avalonia control. Omit x/y/width/height for the whole control or provide all four in local DIPs. Scale is output pixels per DIP (default 1), independent of desktop DPI. Returns the PNG plus actual pixel dimensions, selected local bounds, scale and rendering DPI. Native windows require native-screenshot.")]
    public Task<CallToolResult> UiScreenshot(long nodeId,
        double? x = null,
        double? y = null,
        double? width = null,
        double? height = null,
        double scale = 1,
        CancellationToken cancellationToken = default)
    {
        var coordinateCount = new[] { x, y, width, height }.Count(value => value.HasValue);
        if (coordinateCount is not (0 or 4))
            throw new McpException("invalid_bounds: x, y, width and height must be supplied together.");
        if (new[] { x, y, width, height }.Any(value => value.HasValue && !double.IsFinite(value.Value)) ||
            x < 0 || y < 0 || width <= 0 || height <= 0)
            throw new McpException(
                "invalid_bounds: The screenshot region must have finite, nonnegative coordinates and positive dimensions.");
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(96 * scale) || !double.IsFinite(1 / scale))
            throw new McpException(
                "invalid_scale: Scale must be a finite, positive number that produces a finite rendering DPI.");
        var clip = coordinateCount == 0 ? null : new ScreenshotBounds(x!.Value, y!.Value, width!.Value, height!.Value);
        return managed.WithClientAsync(cancellationToken,
            async (client, token) =>
            {
                var capture = await client
                    .Rendering.CaptureElementAsync(new CaptureElementRequest(managed.NativeNodeId(nodeId), clip, scale),
                        token)
                    .ConfigureAwait(false);
                return new CallToolResult
                {
                    Content =
                    [
                        ImageContentBlock.FromBytes(Convert.FromBase64String(capture.PngBase64), "image/png"),
                        new TextContentBlock
                        {
                            Text = JsonSerializer.Serialize(new
                            {
                                NodeId = nodeId, capture.Width, capture.Height, capture.Bounds,
                                capture.Scale, capture.DpiX, capture.DpiY, CoordinateSpace = "control-local-dip"
                            })
                        }
                    ]
                };
            });
    }

    [McpServerTool(Name = "native-screenshot", ReadOnly = true, Destructive = false),
     Description(
         "Captures a PNG of a native node in the attached application's scoped window, without activating it. Optional x/y/width/height must all be supplied in node-local physical pixels. Uses the window's PrintWindow surface, never an arbitrary desktop region; GPU/protected surfaces may be incomplete. Returns image plus bounds, method and limitations.")]
    public Task<CallToolResult> NativeScreenshot(string nodeId,
        double? x = null,
        double? y = null,
        double? width = null,
        double? height = null,
        int maxPixels = 8_000_000,
        CancellationToken cancellationToken = default) => WithNativeAsync(cancellationToken,
        async (native, token) =>
        {
            var capture = await native.CaptureAsync(new NativeScreenshotRequest
                    { NodeId = nodeId, X = x, Y = y, Width = width, Height = height, MaxPixels = maxPixels },
                token).ConfigureAwait(false);
            return new CallToolResult
            {
                Content =
                [
                    new TextContentBlock
                    {
                        Text = JsonSerializer.Serialize(new
                        {
                            capture.NodeId, capture.Width, capture.Height, capture.ScreenBounds, capture.CaptureMethod,
                            capture.Limitations, CoordinateSpace = "physical-pixels",
                            MaximumEncodedBytes = 16 * 1024 * 1024
                        })
                    },
                    ImageContentBlock.FromBytes(Convert.FromBase64String(capture.PngBase64), "image/png")
                ]
            };
        });
}
