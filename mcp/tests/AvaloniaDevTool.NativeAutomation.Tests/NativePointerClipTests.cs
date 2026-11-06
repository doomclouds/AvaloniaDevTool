using System.Runtime.InteropServices;
using AvaloniaDevTool.NativeAutomation;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed partial class NativeAutomationIntegrationTests
{
    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_pointer_confirms_arrival_and_refuses_clipped_moves_before_button_injection()
    {
        using var fixture = await StartFixtureAsync();
        await using var native = await AttachAsync(fixture);
        var editor = await FindAsync(native, "NativeEditor");
        var button = await FindAsync(native, "NativeInvoke");
        var status = await FindAsync(native, "NativeStatus");
        var bounds = Assert.IsType<NativeBounds>(editor.Bounds);
        var clip = new ClipRectangle
        {
            Left = (int)bounds.X + 2, Top = (int)bounds.Y + 2,
            Right = (int)bounds.X + 12, Bottom = (int)bounds.Y + 12
        };
        // Establish the fixture's foreground activation before applying a clip;
        // Windows can release a previous process's clip during activation.
        Assert.True((await native.SendInputAsync(new NativeInputRequest
                { NodeId = editor.NodeId, Kind = "Move" },
            TestContext.Current.CancellationToken)).Injected);
        Assert.True(ReadPhysicalClip(out var previousClip));
        var applied = false;
        try
        {
            Assert.True(WritePhysicalClip(ref clip));
            applied = true;
            Assert.True(ReadPhysicalClip(out var activeClip));
            Assert.Equal(clip.Left, activeClip.Left);
            Assert.Equal(clip.Top, activeClip.Top);
            Assert.Equal(clip.Right, activeClip.Right);
            Assert.Equal(clip.Bottom, activeClip.Bottom);
            var buttonBounds = Assert.IsType<NativeBounds>(button.Bounds);
            var targetX = buttonBounds.X + buttonBounds.Width / 2;
            var targetY = buttonBounds.Y + buttonBounds.Height / 2;
            Assert.True(targetX < activeClip.Left || targetX >= activeClip.Right ||
                        targetY < activeClip.Top || targetY >= activeClip.Bottom,
                "The clip must exclude the requested button point.");
            var error = await Assert.ThrowsAsync<NativeAutomationException>(() =>
                native.SendInputAsync(new NativeInputRequest
                        { NodeId = button.NodeId, Kind = "DoubleClick" },
                    TestContext.Current.CancellationToken));
            Assert.Equal("input_not_arrived", error.Code);
            Assert.Contains("no buttons or keys were injected", error.Message);
            Assert.Equal("idle",
                (await native.GetElementAsync(status.NodeId, TestContext.Current.CancellationToken)).Name);
        }
        finally
        {
            if (applied)
                Assert.True(WritePhysicalClip(ref previousClip));
        }

        var accepted = await native.SendInputAsync(new NativeInputRequest
                { NodeId = button.NodeId, Kind = "Click" },
            TestContext.Current.CancellationToken);
        Assert.True(accepted.Injected);
        Assert.Equal(3, accepted.InputCount);
        await WaitForStatusAsync(native, "invoked:1");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClipRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClipCursor(out ClipRectangle clip);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClipCursor(ref ClipRectangle clip);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    private static bool ReadPhysicalClip(out ClipRectangle clip)
    {
        var previous = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            return GetClipCursor(out clip);
        }
        finally
        {
            if (previous != 0)
                SetThreadDpiAwarenessContext(previous);
        }
    }

    private static bool WritePhysicalClip(ref ClipRectangle clip)
    {
        var previous = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            return ClipCursor(ref clip);
        }
        finally
        {
            if (previous != 0)
                SetThreadDpiAwarenessContext(previous);
        }
    }
}
