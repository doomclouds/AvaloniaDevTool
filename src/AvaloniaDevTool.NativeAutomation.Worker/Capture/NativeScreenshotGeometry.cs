using AvaloniaDevTool.NativeAutomation;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal readonly record struct NativeScreenshotRegion(
    int SourceX,
    int SourceY,
    int Width,
    int Height,
    NativeBounds ScreenBounds)
{
    internal const int MaximumPixels = 16_000_000;
    internal const int MaximumEncodedBytes = 16 * 1024 * 1024;

    internal static void ValidateRequest(NativeScreenshotRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.NodeId))
            throw new NativeAutomationException("invalid_argument", "A scoped native node ID is required.");
        if (request.MaxPixels is < 1 or > MaximumPixels)
            throw new NativeAutomationException("invalid_argument", $"MaxPixels must be 1..{MaximumPixels}.");
        var count = new[] { request.X, request.Y, request.Width, request.Height }.Count(value => value.HasValue);
        if (count is not (0 or 4))
            throw new NativeAutomationException("invalid_coordinates",
                "Specify X, Y, Width and Height together, or omit all four.");
        if (count == 4 && (!double.IsFinite(request.X!.Value) || !double.IsFinite(request.Y!.Value) ||
                           !double.IsFinite(request.Width!.Value) || !double.IsFinite(request.Height!.Value) ||
                           request.X < 0 || request.Y < 0 || request.Width <= 0 || request.Height <= 0))
            throw new NativeAutomationException("invalid_coordinates",
                "The node-local physical-pixel rectangle must be finite, with nonnegative X/Y and positive Width/Height.");
    }

    internal static NativeScreenshotRegion Create(NativeScreenshotRequest request,
        NativeBounds node,
        NativeBounds window)
    {
        ValidateRequest(request);
        ValidateBounds(node, "The native node has no finite, positive physical-pixel bounds.");
        ValidateBounds(window, "The scoped window has no finite, positive physical-pixel bounds.");
        if (window.X != Math.Floor(window.X) || window.Y != Math.Floor(window.Y) ||
            window.Width != Math.Floor(window.Width) || window.Height != Math.Floor(window.Height) ||
            window.Width * window.Height > MaximumPixels)
            throw new NativeAutomationException("screenshot_source_limit",
                $"The complete scoped window source must use integer physical pixels and fit within {MaximumPixels} pixels.");

        var x = request.X ?? 0;
        var y = request.Y ?? 0;
        var width = request.Width ?? node.Width;
        var height = request.Height ?? node.Height;
        if (x + width > node.Width || y + height > node.Height)
            throw new NativeAutomationException("invalid_coordinates",
                "The requested rectangle must remain inside the native node's current bounds.");
        // UIA bounds may be fractional; include every intersecting physical pixel.
        var left = Math.Floor(node.X + x);
        var top = Math.Floor(node.Y + y);
        var right = Math.Ceiling(node.X + x + width);
        var bottom = Math.Ceiling(node.Y + y + height);
        if (right <= left || bottom <= top)
            throw new NativeAutomationException("invalid_coordinates",
                "The requested rectangle does not cover a representable physical pixel.");
        if (left < window.X || top < window.Y || right > window.X + window.Width || bottom > window.Y + window.Height)
            throw new NativeAutomationException("screenshot_region_out_of_bounds",
                "The requested node region is not entirely inside its scoped window surface. Choose a visible region inside that window.");
        if (right - left > int.MaxValue || bottom - top > int.MaxValue ||
            (right - left) * (bottom - top) > request.MaxPixels)
            throw new NativeAutomationException("screenshot_pixel_limit",
                $"The requested physical-pixel image exceeds MaxPixels ({request.MaxPixels}); request a smaller node-local rectangle.");
        return new NativeScreenshotRegion(checked((int)(left - window.X)),
            checked((int)(top - window.Y)),
            checked((int)(right - left)),
            checked((int)(bottom - top)),
            new NativeBounds(left, top, right - left, bottom - top));
    }

    private static void ValidateBounds(NativeBounds bounds, string message)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || !double.IsFinite(bounds.Width) ||
            !double.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0 ||
            !double.IsFinite(bounds.X + bounds.Width) || !double.IsFinite(bounds.Y + bounds.Height) ||
            bounds.X < int.MinValue || bounds.Y < int.MinValue || bounds.X + bounds.Width > int.MaxValue ||
            bounds.Y + bounds.Height > int.MaxValue)
            throw new NativeAutomationException("screenshot_unavailable", message);
    }
}
