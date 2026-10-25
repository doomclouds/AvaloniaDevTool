using System.ComponentModel;
using System.Text.Json;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class AgentTools
{
    [McpServerTool(Name = "ui-screenshot", ReadOnly = true, Destructive = false), Description("Captures actual PNG pixels from a canonical Avalonia control. Omit x/y/width/height for the whole control or provide all four in local DIPs. Scale is output pixels per DIP (default 1), independent of desktop DPI. Returns the PNG plus actual pixel dimensions, selected local bounds, scale and rendering DPI. Native windows require native-screenshot.")]
    public Task<CallToolResult> UiScreenshot(long nodeId, double? x = null, double? y = null,
        double? width = null, double? height = null, double scale = 1, CancellationToken cancellationToken = default)
    {
        var coordinateCount = new[] { x, y, width, height }.Count(value => value.HasValue);
        if (coordinateCount is not (0 or 4))
            throw new McpException("invalid_bounds: x, y, width and height must be supplied together.");
        if (new[] { x, y, width, height }.Any(value => value.HasValue && !double.IsFinite(value.Value)) ||
            x < 0 || y < 0 || width <= 0 || height <= 0)
            throw new McpException("invalid_bounds: The screenshot region must have finite, nonnegative coordinates and positive dimensions.");
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(96 * scale) || !double.IsFinite(1 / scale))
            throw new McpException("invalid_scale: Scale must be a finite, positive number that produces a finite rendering DPI.");
        var clip = coordinateCount == 0 ? null : new ScreenshotBounds(x!.Value, y!.Value, width!.Value, height!.Value);
        return official.WithAgentClientAsync(cancellationToken, async (client, token) =>
        {
            var capture = await client.CaptureElementAsync(new CaptureElementRequest(official.AgentNativeNodeId(nodeId), clip, scale), token)
                .ConfigureAwait(false);
            return new CallToolResult
            {
                Content =
                [
                    ImageContentBlock.FromBytes(Convert.FromBase64String(capture.PngBase64), "image/png"),
                    new TextContentBlock { Text = JsonSerializer.Serialize(new
                    {
                        NodeId = nodeId, capture.Width, capture.Height, capture.Bounds,
                        capture.Scale, capture.DpiX, capture.DpiY, CoordinateSpace = "control-local-dip"
                    }) }
                ]
            };
        });
    }
}
