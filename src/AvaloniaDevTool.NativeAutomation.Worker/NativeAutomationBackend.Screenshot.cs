using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AvaloniaDevTool.NativeAutomation;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal sealed partial class NativeAutomationBackend
{
    private const string PrintWindowFallback = "For an attached Avalonia control, use ui-screenshot. Native surfaces that do not render through PrintWindow are unsupported; a desktop capture is not substituted.";

    private NativeScreenshotResult Capture(NativeScreenshotRequest request)
    {
        NativeScreenshotRegion.ValidateRequest(request);
        using var coordinates = NativeCoordinateSpace.Enter();
        var entry = Resolve(request.NodeId);
        if (!NativeWindows.IsWindowVisible(entry.Root.Handle) || IsIconic(entry.Root.Handle) ||
            entry.Element.Properties.IsOffscreen.ValueOrDefault)
            throw new NativeAutomationException("screenshot_unavailable", "The native node must belong to a visible, non-minimized scoped window. Screenshot does not activate or restore windows.");
        var nodeBounds = ReadScreenshotNodeBounds(entry);
        var windowBounds = ReadScreenshotWindowBounds(entry.Root.Handle);
        var region = NativeScreenshotRegion.Create(request, nodeBounds, windowBounds);
        ValidateEntry(entry);
        using var source = new Bitmap(checked((int)windowBounds.Width), checked((int)windowBounds.Height), PixelFormat.Format32bppRgb);
        var crop = new Rectangle(region.SourceX, region.SourceY, region.Width, region.Height);
        var firstMarker = Color.FromArgb(37, 73, 109);
        var secondMarker = Color.FromArgb(211, 167, 53);
        RenderScreenshot(entry, source, firstMarker);
        using (var first = source.Clone(crop, PixelFormat.Format32bppRgb))
        {
            ValidateEntry(entry);
            RenderScreenshot(entry, source, secondMarker);
            // Only the marker pair at the same pixel establishes an unpainted pixel.
            // Different real frame pixels, including legitimate black, remain valid.
            if (ScreenshotContainsUnpaintedPixels(first, source, crop, firstMarker, secondMarker))
                throw new NativeAutomationException("screenshot_not_rendered", "PrintWindow left pixels in the requested region untouched across two different initial fills. " + PrintWindowFallback);
        }
        using var image = source.Clone(crop, PixelFormat.Format32bppRgb);
        using var encoded = new ScreenshotPngStream(NativeScreenshotRegion.MaximumEncodedBytes);
        try { image.Save(encoded, ImageFormat.Png); }
        catch (Exception error) when (encoded.LimitExceeded)
        { throw new NativeAutomationException("screenshot_encoded_limit", $"The complete PNG exceeds {NativeScreenshotRegion.MaximumEncodedBytes} bytes; request a smaller node-local rectangle.", error); }
        if (encoded.Length == 0)
            throw new NativeAutomationException("screenshot_encoding_failed", "The window capture produced no PNG bytes.");
        var pngBase64 = Convert.ToBase64String(encoded.GetBuffer(), 0, checked((int)encoded.Length));
        ValidateEntry(entry);
        if (ReadScreenshotWindowBounds(entry.Root.Handle) != windowBounds || ReadScreenshotNodeBounds(entry) != nodeBounds)
            throw new NativeAutomationException("screenshot_target_changed", "The native node or its scoped window moved or resized during capture. Inspect the node and retry.");
        ValidateLifetimes(entry);
        return new NativeScreenshotResult(request.NodeId, pngBase64, region.Width, region.Height,
            region.ScreenBounds, "PrintWindow(PW_RENDERFULLCONTENT)",
            [
                "PrintWindow depends on the target renderer. GPU, protected, layered or embedded content may be black or incomplete even when the API succeeds; pixel colors do not prove complete rendering.",
                "The image comes from the scoped window surface without desktop fallback or focus changes. Coordinates are physical pixels; fractional node bounds are rounded outward."
            ]);
    }

    private static NativeBounds ReadScreenshotNodeBounds(NodeEntry entry)
    {
        var bounds = entry.Element.Properties.BoundingRectangle.Value;
        return new NativeBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static NativeBounds ReadScreenshotWindowBounds(nint handle)
    {
        if (!GetScreenshotWindowRect(handle, out var rectangle))
            throw new NativeAutomationException("screenshot_unavailable", $"Windows could not read the scoped window's physical bounds (Win32={Marshal.GetLastWin32Error()}).");
        return new NativeBounds(rectangle.Left, rectangle.Top,
            (long)rectangle.Right - rectangle.Left, (long)rectangle.Bottom - rectangle.Top);
    }

    private void RenderScreenshot(NodeEntry entry, Bitmap source, Color marker)
    {
        ValidateLifetimes(entry);
        using var graphics = Graphics.FromImage(source);
        graphics.Clear(marker);
        var dc = graphics.GetHdc();
        bool rendered;
        try { rendered = PrintScreenshotWindow(entry.Root.Handle, dc, 2); }
        finally { graphics.ReleaseHdc(dc); }
        ValidateLifetimes(entry);
        if (!rendered)
            throw new NativeAutomationException("screenshot_capture_failed", "Windows PrintWindow did not render the scoped window. " + PrintWindowFallback);
    }

    private static bool ScreenshotContainsUnpaintedPixels(Bitmap first, Bitmap second, Rectangle region,
        Color firstMarker, Color secondMarker)
    {
        var firstData = first.LockBits(new Rectangle(0, 0, region.Width, region.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var secondData = second.LockBits(region, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                var firstRow = new byte[checked(region.Width * 4)];
                var secondRow = new byte[firstRow.Length];
                for (var y = 0; y < region.Height; y++)
                {
                    Marshal.Copy(firstData.Scan0 + y * firstData.Stride, firstRow, 0, firstRow.Length);
                    Marshal.Copy(secondData.Scan0 + y * secondData.Stride, secondRow, 0, secondRow.Length);
                    for (var x = 0; x < firstRow.Length; x += 4)
                        if (firstRow[x] == firstMarker.B && firstRow[x + 1] == firstMarker.G && firstRow[x + 2] == firstMarker.R &&
                            secondRow[x] == secondMarker.B && secondRow[x + 1] == secondMarker.G && secondRow[x + 2] == secondMarker.R) return true;
                }
                return false;
            }
            finally { second.UnlockBits(secondData); }
        }
        finally { first.UnlockBits(firstData); }
    }

    private sealed class ScreenshotPngStream(int maximumBytes) : MemoryStream
    {
        internal bool LimitExceeded { get; private set; }
        private void VerifySize(long size)
        {
            if (size <= maximumBytes) return;
            LimitExceeded = true;
            throw new NativeAutomationException("screenshot_encoded_limit", $"The complete PNG exceeds {maximumBytes} bytes.");
        }
        public override void Write(byte[] buffer, int offset, int count)
        { VerifySize(Position + count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer)
        { VerifySize(Position + buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value)
        { VerifySize(Position + 1); base.WriteByte(value); }
        public override void SetLength(long value)
        { VerifySize(value); base.SetLength(value); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenshotWindowRect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetScreenshotWindowRect(nint window, out ScreenshotWindowRect rectangle);
    [DllImport("user32.dll", EntryPoint = "PrintWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintScreenshotWindow(nint window, nint dc, uint flags);
}
