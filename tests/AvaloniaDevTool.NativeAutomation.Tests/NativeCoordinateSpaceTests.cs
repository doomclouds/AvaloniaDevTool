using System.Runtime.InteropServices;
using AvaloniaDevTool.NativeAutomation.Worker;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed class NativeCoordinateSpaceTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Windows DPI coordinate contexts are required.")]
    public void
        Physical_normalization_is_identical_from_unaware_system_and_per_monitor_threads_and_restores_each_context()
    {
        (int X, int Y, int Left, int Top, int Width, int Height) expected;
        var previous = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            expected = NativeCoordinateSpace.Normalize(GetSystemMetrics(76) + GetSystemMetrics(78) / 2,
                GetSystemMetrics(77) + GetSystemMetrics(79) / 2);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }

        foreach (var context in new[] { -1, -2, -3, -4 })
        {
            previous = SetThreadDpiAwarenessContext(new nint(context));
            try
            {
                var awareness = GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext());
                var actual = NativeCoordinateSpace.Normalize(expected.Left + expected.Width / 2,
                    expected.Top + expected.Height / 2);
                Assert.Equal(expected, actual);
                Assert.Equal(awareness, GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()));
                var error = Assert.Throws<NativeAutomationException>(() => NativeCoordinateSpace.Normalize(
                    expected.Left + expected.Width,
                    expected.Top));
                Assert.Equal("invalid_coordinates", error.Code);
                Assert.Equal(awareness, GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()));
            }
            finally
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern int GetAwarenessFromDpiAwarenessContext(nint context);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int metric);
}
