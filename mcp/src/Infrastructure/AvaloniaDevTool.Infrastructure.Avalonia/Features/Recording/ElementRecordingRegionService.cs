using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Resolves a fixed recording region without rendering, highlighting or changing application state.</summary>
internal sealed class ElementRecordingRegionService(WeakObjectRegistry objects, Func<object, long> observe)
{
    public ElementRecordingRegion Resolve(ElementRecordingRegionRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (request.NodeIds is not { Count: >= 1 and <= 16 })
            throw new RpcException("invalid_recording_region", "Select between 1 and 16 controls.");
        if (!double.IsFinite(request.PaddingDip) || request.PaddingDip is < 0 or > 256)
            throw new RpcException("invalid_recording_region", "Padding must be finite and between 0 and 256 DIP.");

        var ids = request.NodeIds.Distinct().ToArray();
        var controls = ids.Select(id => objects.Get(id) as Control ??
            throw new RpcException("unsupported_object", "Recording regions require application controls.")).ToArray();
        var geometry = ResolveGeometry(controls, request.PaddingDip);
        if (!OperatingSystem.IsWindows() ||
            geometry.Root.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle || handle.Handle == 0)
            throw new RpcException("unsupported_capture_window", "The selected controls need a native Windows HWND.");

        var hwnd = handle.Handle;
        if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid != Environment.ProcessId || IsIconic(hwnd))
            throw new RpcException("capture_window_unavailable", "The selected window is unavailable or minimized.");
        if (!GetWindowRect(hwnd, out var window) || !GetClientRect(hwnd, out var client))
            throw new RpcException("capture_geometry_unavailable", "Windows could not resolve the window bounds.");
        var origin = new NativePoint();
        if (!ClientToScreen(hwnd, ref origin))
            throw new RpcException("capture_geometry_unavailable", "Windows could not resolve the client origin.");
        var clientBounds = FromNative(client, origin.X, origin.Y);
        var windowBounds = FromNative(window, window.Left, window.Top);
        var scale = geometry.Root.RenderScaling;
        if (!double.IsFinite(scale) || scale <= 0)
            throw new RpcException("capture_geometry_unavailable", "The window has no finite positive rendering scale.");
        var rootOrigin = geometry.Root.PointToScreen(default);
        if (Math.Abs((long)rootOrigin.X - origin.X) > 2 || Math.Abs((long)rootOrigin.Y - origin.Y) > 2)
            throw new RpcException("capture_geometry_unavailable", "The Avalonia root does not map to the native client origin.");

        var region = ToPixelBounds(geometry.Bounds, scale, clientBounds.Width, clientBounds.Height);
        var screen = region with { X = checked(origin.X + region.X), Y = checked(origin.Y + region.Y) };
        using var process = Process.GetCurrentProcess();
        return new ElementRecordingRegion(Environment.ProcessId,
            new DateTimeOffset(process.StartTime.ToUniversalTime()),
            hwnd.ToInt64(),
            observe(geometry.Root),
            ids,
            windowBounds,
            clientBounds,
            region,
            screen,
            scale,
            geometry.IsClipped,
            DateTimeOffset.UtcNow,
            geometry.Limitations);
    }

    internal static RegionGeometry ResolveGeometry(IReadOnlyList<Control> controls, double paddingDip)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (controls.Count is < 1 or > 16 || !double.IsFinite(paddingDip) || paddingDip is < 0 or > 256)
            throw new RpcException("invalid_recording_region", "Select 1..16 controls and padding between 0 and 256 DIP.");
        TopLevel? root = null;
        Rect? combined = null;
        var clipped = false;
        var limitations = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in controls)
        {
            if (OverlayService.IsDiagnosticVisual(control))
                throw new RpcException("unsupported_object", "Diagnostic overlays are not recording targets.");
            if (!control.IsAttachedToVisualTree() || TopLevel.GetTopLevel(control) is not { } owner)
                throw new RpcException("detached_object", "Attach the control to a visible window before recording.");
            if (root is not null && !ReferenceEquals(root, owner))
                throw new RpcException("recording_multiple_windows", "Select controls from the same top-level window.");
            root = owner;
            if (!control.IsEffectivelyVisible || !root.IsVisible)
                throw new RpcException("hidden_target", "Every selected control must be visible.");
            if (!FinitePositive(control.Bounds.Width) || !FinitePositive(control.Bounds.Height) ||
                !FinitePositive(root.Bounds.Width) || !FinitePositive(root.Bounds.Height))
                throw new RpcException("invalid_layout", "Selected controls must have finite positive arranged bounds.");

            var bounds = TransformBounds(control, root, new Rect(control.Bounds.Size));
            var visible = bounds.Intersect(new Rect(root.Bounds.Size));
            var count = 0;
            for (Visual? ancestor = control; ancestor is not null; ancestor = ancestor.GetVisualParent())
            {
                if (++count > 1024)
                    throw new RpcException("recording_geometry_limit", "The visual ancestor chain exceeds the recording limit.");
                if (ancestor.ClipToBounds)
                    visible = visible.Intersect(TransformBounds(ancestor, root, new Rect(ancestor.Bounds.Size)));
                if (ancestor.Clip is { } clip)
                {
                    if (clip.Transform is { } clipTransform && !OverlayGeometry.FiniteAffine(clipTransform.Value))
                        throw new RpcException("unsupported_transform", "A recording clip has a non-finite transform.");
                    // Geometry.Bounds already includes the geometry's own Transform.
                    visible = visible.Intersect(TransformBounds(ancestor, root, clip.Bounds));
                    limitations.Add("clip_geometry_bounds: Nonrectangular clips use a conservative rectangular recording region.");
                }
                if (ReferenceEquals(ancestor, root))
                    break;
            }
            if (!FinitePositive(visible.Width) || !FinitePositive(visible.Height))
                throw new RpcException("recording_region_not_visible", "A selected control has no visible area in its window.");
            clipped |= visible != bounds;
            combined = combined is { } previous ? previous.Union(visible) : visible;
        }

        var padded = combined!.Value.Inflate(paddingDip);
        var result = padded.Intersect(new Rect(root!.Bounds.Size));
        clipped |= result != padded;
        return new RegionGeometry(root, result, clipped, limitations.ToArray());
    }

    internal static RecordingPixelRect ToPixelBounds(Rect bounds, double scale, int clientWidth, int clientHeight)
    {
        if (!double.IsFinite(scale) || scale <= 0 || clientWidth <= 0 || clientHeight <= 0)
            throw new RpcException("capture_geometry_unavailable", "The physical client bounds or scale are invalid.");
        var left = Math.Clamp(Math.Floor(bounds.Left * scale), 0, clientWidth);
        var top = Math.Clamp(Math.Floor(bounds.Top * scale), 0, clientHeight);
        var right = Math.Clamp(Math.Ceiling(bounds.Right * scale), 0, clientWidth);
        var bottom = Math.Clamp(Math.Ceiling(bounds.Bottom * scale), 0, clientHeight);
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom) ||
            right <= left || bottom <= top)
            throw new RpcException("recording_region_not_visible", "The selected region has no physical pixels in the window.");
        return new RecordingPixelRect((int)left, (int)top, (int)(right - left), (int)(bottom - top));
    }

    private static Rect TransformBounds(Visual visual, TopLevel root, Rect bounds)
    {
        if (visual.TransformToVisual(root) is not { } transform || !OverlayGeometry.FiniteAffine(transform))
            throw new RpcException("unsupported_transform", "The selected visual cannot be mapped to its window.");
        return Bounds(OverlayGeometry.Points(OverlayGeometry.Quad(bounds, transform)));
    }

    private static Rect Bounds(IEnumerable<OverlayPoint> points)
    {
        var values = points.ToArray();
        if (values.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
            throw new RpcException("unsupported_transform", "The recording transform produced invalid coordinates.");
        return new Rect(new Point(values.Min(point => point.X), values.Min(point => point.Y)),
            new Point(values.Max(point => point.X), values.Max(point => point.Y)));
    }

    private static RecordingPixelRect FromNative(NativeRect rectangle, int x, int y)
    {
        var width = (long)rectangle.Right - rectangle.Left;
        var height = (long)rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
            throw new RpcException("capture_geometry_unavailable", "The native window has invalid physical bounds.");
        return new RecordingPixelRect(x, y, (int)width, (int)height);
    }

    private static bool FinitePositive(double value) => double.IsFinite(value) && value > 0;

    internal sealed record RegionGeometry(TopLevel Root, Rect Bounds, bool IsClipped, IReadOnlyList<string> Limitations);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out NativeRect rectangle);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);
}
