using System.Runtime.InteropServices;
using AvaloniaDevTool.NativeAutomation;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal static class NativeCoordinateSpace
{
    internal static IDisposable Enter()
    {
        var previous = SetThreadDpiAwarenessContext(new nint(-4));
        if (previous == 0)
            throw new NativeAutomationException("dpi_context_unavailable", "Windows could not establish the physical coordinate context.");
        return new Scope(previous);
    }

    internal static (int X, int Y, int Left, int Top, int Width, int Height) Normalize(int x, int y)
    {
        using var scope = Enter();
        var left = GetSystemMetrics(76);
        var top = GetSystemMetrics(77);
        var width = GetSystemMetrics(78);
        var height = GetSystemMetrics(79);
        if (width <= 1 || height <= 1 || x < left || y < top || x >= left + width || y >= top + height)
            throw new NativeAutomationException("invalid_coordinates", "The scoped input point is outside the current virtual desktop.");
        return ((int)Math.Round((x - left) * 65535d / (width - 1)),
            (int)Math.Round((y - top) * 65535d / (height - 1)), left, top, width, height);
    }

    internal static bool ReadCursor(out int x, out int y)
    {
        using var scope = Enter();
        var success = GetPhysicalCursorPos(out var point);
        x = point.X;
        y = point.Y;
        return success;
    }

    internal static nint WindowAt(int x, int y)
    {
        using var scope = Enter();
        return WindowFromPoint(new PixelPoint { X = x, Y = y });
    }

    private sealed class Scope(nint previous) : IDisposable
    {
        public void Dispose() => SetThreadDpiAwarenessContext(previous);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PixelPoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetPhysicalCursorPos(out PixelPoint point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(PixelPoint point);
}
