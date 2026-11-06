using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed class ElementScreenshotService
{
    private readonly WeakObjectRegistry _objects;
    private readonly OverlayService _overlay;
    private readonly Action<long> _validateAttachment;

    internal ElementScreenshotService(WeakObjectRegistry objects,
        OverlayService overlay,
        Action<long> validateAttachment)
    {
        _objects = objects;
        _overlay = overlay;
        _validateAttachment = validateAttachment;
    }

    private const int MaximumScreenshotEdge = 2048;

    private const long MaximumScreenshotPixels = 2L * 1024 * 1024;

    private const int MaximumScreenshotBytes = 1024 * 1024;

    internal ElementScreenshot Capture(CaptureElementRequest request)
    {
        if (_objects.Get(request.ObjectId) is not Control control)
            throw new RpcException("unsupported_object", "Screenshots require a control.");
        if (TopLevel.GetTopLevel(control) is not { IsVisible: true })
            throw new RpcException("object_detached", "Screenshots require a control in a visible window.");
        _validateAttachment(request.ObjectId);
        if (!control.IsEffectivelyVisible)
            throw new RpcException("hidden_target", "Screenshots require an effectively visible control.");
        var size = control.Bounds.Size;
        if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height) || size.Width <= 0 || size.Height <= 0)
            throw new RpcException("invalid_bounds", "The control must have finite, positive arranged bounds.");
        var clip = request.Clip ?? new ScreenshotBounds(0, 0, size.Width, size.Height);
        if (!double.IsFinite(clip.X) || !double.IsFinite(clip.Y) || !double.IsFinite(clip.Width) ||
            !double.IsFinite(clip.Height) ||
            clip.X < 0 || clip.Y < 0 || clip.Width <= 0 || clip.Height <= 0 ||
            clip.X > size.Width || clip.Y > size.Height || clip.Width > size.Width - clip.X ||
            clip.Height > size.Height - clip.Y)
            throw new RpcException("invalid_bounds",
                "The screenshot region must have finite, positive dimensions and fit inside the control's local bounds.");
        if (!double.IsFinite(request.Scale) || request.Scale <= 0 ||
            !double.IsFinite(96 * request.Scale) || !double.IsFinite(1 / request.Scale))
            throw new RpcException("invalid_scale",
                "Scale must be a finite, positive number that produces a finite rendering DPI.");
        var width = Math.Ceiling(clip.Width * request.Scale);
        var height = Math.Ceiling(clip.Height * request.Scale);
        if (!double.IsFinite(width) || !double.IsFinite(height) || width > MaximumScreenshotEdge ||
            height > MaximumScreenshotEdge ||
            width * height > MaximumScreenshotPixels)
            throw new RpcException("screenshot_limit",
                "A screenshot may have at most a 2048-pixel edge and 2,097,152 pixels.");
        if (width < 1 || height < 1 || !double.IsFinite(width / request.Scale) ||
            !double.IsFinite(height / request.Scale))
            throw new RpcException("invalid_scale", "Scale must produce finite, positive rendered dimensions.");

        using var suspendedOverlay = _overlay.Suspend();
        try
        {
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)width, (int)height),
                new Vector(96 * request.Scale, 96 * request.Scale));
            if (request.Clip is null)
                bitmap.Render(control);
            else
            {
                // VisualBrush renders the actual subtree and selects its local source region before rasterization.
                using var context = bitmap.CreateDrawingContext();
                var destination = new Rect(0, 0, clip.Width, clip.Height);
                context.DrawRectangle(new VisualBrush(control)
                    {
                        SourceRect = new RelativeRect(clip.X, clip.Y, clip.Width, clip.Height, RelativeUnit.Absolute),
                        DestinationRect = new RelativeRect(destination, RelativeUnit.Absolute),
                        Stretch = Stretch.Fill,
                        TileMode = TileMode.None
                    },
                    null,
                    destination);
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            if (stream.Length > MaximumScreenshotBytes)
                throw new RpcException("screenshot_limit", "The encoded PNG exceeds the 1 MiB screenshot limit.");
            return new ElementScreenshot(bitmap.PixelSize.Width,
                bitmap.PixelSize.Height,
                Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length),
                clip,
                bitmap.Dpi.X / 96,
                bitmap.Dpi.X,
                bitmap.Dpi.Y);
        }
        catch (NotSupportedException exception)
        {
            throw new RpcException("unsupported_renderer",
                "The current renderer cannot capture this control: " + exception.Message);
        }
        catch (Exception exception) when (exception is not RpcException)
        {
            throw new RpcException("screenshot_failed", "The control could not be captured: " + exception.Message);
        }
    }
}
