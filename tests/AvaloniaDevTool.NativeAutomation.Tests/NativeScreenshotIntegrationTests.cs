using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using AvaloniaDevTool.NativeAutomation;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed partial class NativeAutomationIntegrationTests
{
    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_screenshots_use_scoped_physical_pixels_current_content_and_preserve_foreground()
    {
        using var fixture = await StartFixtureAsync();
        await using var native = await AttachAsync(fixture);
        var main = Assert.Single(await native.GetWindowsAsync(TestContext.Current.CancellationToken),
            window => window.Title == "AVDT Native Scope Fixture");
        var panel = await FindAsync(native, "NativePointer");
        var panelBounds = Assert.IsType<NativeBounds>(panel.Bounds);
        var foreground = GetScreenshotForegroundWindow();
        var request = new NativeScreenshotRequest { NodeId = panel.NodeId, X = 5, Y = 5, Width = 32, Height = 16 };
        var capture = await native.CaptureAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(foreground, GetScreenshotForegroundWindow());
        Assert.Equal(panel.NodeId, capture.NodeId);
        Assert.Equal(32, capture.Width);
        Assert.Equal(16, capture.Height);
        Assert.Equal(new NativeBounds(Math.Floor(panelBounds.X + 5), Math.Floor(panelBounds.Y + 5), 32, 16),
            capture.ScreenBounds);
        Assert.Equal("PrintWindow(PW_RENDERFULLCONTENT)", capture.CaptureMethod);
        Assert.Contains(capture.Limitations, note => note.Contains("GPU", StringComparison.Ordinal));
        var bytes = Convert.FromBase64String(capture.PngBase64);
        Assert.InRange(bytes.Length, 1, 16 * 1024 * 1024);
        var image = DecodeScreenshotPng(bytes);
        Assert.Equal((32, 16), (image.Width, image.Height));
        for (var pixel = 0; pixel < image.Rgb.Length; pixel += 3)
        {
            Assert.Equal((byte)173, image.Rgb[pixel]);
            Assert.Equal((byte)216, image.Rgb[pixel + 1]);
            Assert.Equal((byte)230, image.Rgb[pixel + 2]);
        }

        var outputDirectory =
            Path.Combine(Path.GetTempPath(), "AvaloniaDevTool-native-screenshot-" + Environment.ProcessId);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "native-panel-roi.png"),
            bytes,
            TestContext.Current.CancellationToken);
        var fullPanel = await native.CaptureAsync(new NativeScreenshotRequest { NodeId = panel.NodeId },
            TestContext.Current.CancellationToken);
        Assert.Equal((int)(Math.Ceiling(panelBounds.X + panelBounds.Width) - Math.Floor(panelBounds.X)),
            fullPanel.Width);
        Assert.Equal((int)(Math.Ceiling(panelBounds.Y + panelBounds.Height) - Math.Floor(panelBounds.Y)),
            fullPanel.Height);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "native-panel.png"),
            Convert.FromBase64String(fullPanel.PngBase64),
            TestContext.Current.CancellationToken);

        Assert.Equal("invalid_coordinates",
            (await Assert.ThrowsAsync<NativeAutomationException>(() => native.CaptureAsync(request with
                {
                    X = panelBounds.Width - 1, Width = 2
                },
                TestContext.Current.CancellationToken))).Code);
        Assert.Equal("invalid_coordinates",
            (await Assert.ThrowsAsync<NativeAutomationException>(() => native.CaptureAsync(new NativeScreenshotRequest
                    { NodeId = panel.NodeId, X = 5 },
                TestContext.Current.CancellationToken))).Code);
        Assert.Equal("screenshot_pixel_limit",
            (await Assert.ThrowsAsync<NativeAutomationException>(() => native.CaptureAsync(request with
                {
                    MaxPixels = 511
                },
                TestContext.Current.CancellationToken))).Code);
        Assert.Equal("stale_node",
            (await Assert.ThrowsAsync<NativeAutomationException>(() => native.CaptureAsync(new NativeScreenshotRequest
                    { NodeId = "native:another-session:node" },
                TestContext.Current.CancellationToken))).Code);

        var editor = await FindAsync(native, "NativeEditor");
        var editorRequest = new NativeScreenshotRequest { NodeId = editor.NodeId };
        foreground = GetScreenshotForegroundWindow();
        var before = await native.CaptureAsync(editorRequest, TestContext.Current.CancellationToken);
        Assert.Equal(foreground, GetScreenshotForegroundWindow());
        Assert.Equal("completed",
            (await CompleteAsync(native,
                new NativeActionRequest
                    { NodeId = editor.NodeId, Action = "SetValue", Value = "fresh screenshot 中文" })).Status);
        Assert.Equal("fresh screenshot 中文",
            (await native.GetElementAsync(editor.NodeId, TestContext.Current.CancellationToken)).Value);
        foreground = GetScreenshotForegroundWindow();
        var after = await native.CaptureAsync(editorRequest, TestContext.Current.CancellationToken);
        Assert.Equal(foreground, GetScreenshotForegroundWindow());
        Assert.NotEqual(before.PngBase64, after.PngBase64);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "native-editor-current.png"),
            Convert.FromBase64String(after.PngBase64),
            TestContext.Current.CancellationToken);

        await CompleteAsync(native,
            new NativeActionRequest { NodeId = (await FindAsync(native, "NativeModal")).NodeId, Action = "Invoke" });
        var owned = await WaitForWindowAsync(native, "AVDT Owned Modal");
        Assert.Equal(main.Handle, owned.OwnerHandle);
        foreground = GetScreenshotForegroundWindow();
        var ownedCapture = await native.CaptureAsync(new NativeScreenshotRequest
                { NodeId = owned.NodeId, X = 20, Y = 70, Width = 40, Height = 20 },
            TestContext.Current.CancellationToken);
        Assert.Equal(foreground, GetScreenshotForegroundWindow());
        Assert.Equal((40, 20), (ownedCapture.Width, ownedCapture.Height));
        await CompleteAsync(native,
            new NativeActionRequest
                { NodeId = (await FindAsync(native, "NativeModalClose")).NodeId, Action = "Invoke" });
        var timer = Stopwatch.StartNew();
        while ((await native.GetWindowsAsync(TestContext.Current.CancellationToken)).Any(window =>
                   window.NodeId == owned.NodeId) && timer.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20, TestContext.Current.CancellationToken);
        var stale = await Assert.ThrowsAsync<NativeAutomationException>(() =>
            native.CaptureAsync(new NativeScreenshotRequest
                    { NodeId = owned.NodeId },
                TestContext.Current.CancellationToken));
        Assert.True(stale.Code is "stale_node" or "out_of_scope", $"{stale.Code}: {stale.Message}");
        Console.WriteLine("Native screenshot evidence: " + outputDirectory);
    }

    // This bounded test decoder supports the RGB/RGBA PNGs produced by GDI+.
    private static (int Width, int Height, byte[] Rgb) DecodeScreenshotPng(byte[] png)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var width = 0;
        var height = 0;
        var channels = 0;
        using var compressed = new MemoryStream();
        for (var offset = 8; offset + 12 <= png.Length;)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4)));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            Assert.InRange(length, 0, png.Length - offset - 12);
            var payload = png.AsSpan(offset + 8, length);
            if (type == "IHDR")
            {
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload[..4]));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)));
                Assert.Equal(8, payload[8]);
                channels = payload[9] switch
                {
                    2 => 3, 6 => 4, _ => throw new InvalidDataException("Expected an RGB or RGBA PNG.")
                };
                Assert.Equal(0, payload[10]);
                Assert.Equal(0, payload[11]);
                Assert.Equal(0, payload[12]);
            }
            else if (type == "IDAT")
                compressed.Write(payload);
            else if (type == "IEND")
                break;

            offset += length + 12;
        }

        Assert.InRange(width, 1, 4096);
        Assert.InRange(height, 1, 4096);
        Assert.True(channels is 3 or 4);
        compressed.Position = 0;
        using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
        var stride = checked(width * channels);
        var previous = new byte[stride];
        var current = new byte[stride];
        var rgb = new byte[checked(width * height * 3)];
        for (var y = 0; y < height; y++)
        {
            var filter = inflater.ReadByte();
            inflater.ReadExactly(current);
            for (var x = 0; x < current.Length; x++)
            {
                var left = x >= channels ? current[x - channels] : 0;
                var above = previous[x];
                var upperLeft = x >= channels ? previous[x - channels] : 0;
                var predictor = filter switch
                {
                    0 => 0, 1 => left, 2 => above, 3 => (left + above) / 2,
                    4 => ScreenshotPaeth(left, above, upperLeft),
                    _ => throw new InvalidDataException("Unknown PNG filter.")
                };
                current[x] = unchecked((byte)(current[x] + predictor));
            }

            for (var x = 0; x < width; x++)
                current.AsSpan(x * channels, 3).CopyTo(rgb.AsSpan((y * width + x) * 3, 3));
            (previous, current) = (current, previous);
        }

        Assert.Equal(-1, inflater.ReadByte());
        return (width, height, rgb);
    }

    private static int ScreenshotPaeth(int left, int above, int upperLeft)
    {
        var value = left + above - upperLeft;
        var leftDistance = Math.Abs(value - left);
        var aboveDistance = Math.Abs(value - above);
        var diagonalDistance = Math.Abs(value - upperLeft);
        return leftDistance <= aboveDistance && leftDistance <= diagonalDistance ? left :
            aboveDistance <= diagonalDistance ? above : upperLeft;
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint GetScreenshotForegroundWindow();
}
