using System.Diagnostics;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed partial class NativeFileDialogMcpTests
{
    [Fact(SkipUnless = nameof(IsInteractiveWindows),
        Skip = "Window screenshots require an interactive Windows desktop.")]
    public async Task Stdio_screenshots_return_real_managed_and_native_region_pixels()
    {
        await using var fixture = await SampleProcess.StartAsync();
        await using var client = await CreateClientAsync(fixture.PipeName);
        var editorId = await FindManaged(client, "InputTextBox");
        var property = await Call(client,
            "set-prop",
            ("nodeId", editorId),
            ("propertyName", "Background"),
            ("value", "#FF12AB34"));
        Assert.False(property.IsError == true, Text(property));
        var managed = await Call(client,
            "ui-screenshot",
            ("nodeId", editorId),
            ("x", 8),
            ("y", 4),
            ("width", 16),
            ("height", 8));
        using (var bitmap = DecodeScreenshot(managed))
        {
            Assert.Equal(16, bitmap.Width);
            Assert.Equal(8, bitmap.Height);
            Assert.Contains(bitmap.Pixels, pixel => pixel.Red == 0x12 && pixel.Green == 0xAB && pixel.Blue == 0x34);
        }

        await SaveScreenshotAsync(fixture, "managed-control-region.png", managed);
        var main = Assert.Single(Read<NativeWindowInfo[]>(await Call(client, "native-windows")),
            item => item.ProcessId == fixture.ProcessId && item.Title == "AvaloniaDevTool automation acceptance");
        var editor = await FindNative(client, main.NodeId, "InputTextBox");
        CallToolResult? native = null;
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            native = await Call(client,
                "native-screenshot",
                ("nodeId", editor.NodeId),
                ("x", 16),
                ("y", 8),
                ("width", 16),
                ("height", 8));
            using var bitmap = DecodeScreenshot(native);
            Assert.Equal(16, bitmap.Width);
            Assert.Equal(8, bitmap.Height);
            if (bitmap.Pixels.Any(pixel => pixel.Red == 0x12 && pixel.Green == 0xAB && pixel.Blue == 0x34))
                break;
            await Task.Delay(40, Token);
        }

        using (var bitmap = DecodeScreenshot(native!))
            Assert.Contains(bitmap.Pixels, pixel => pixel.Red == 0x12 && pixel.Green == 0xAB && pixel.Blue == 0x34);
        await SaveScreenshotAsync(fixture, "native-control-region.png", native!);
        var whole = await Call(client, "native-screenshot", ("nodeId", main.NodeId));
        using (var bitmap = DecodeScreenshot(whole))
        {
            Assert.True(bitmap.Width > 400);
            Assert.True(bitmap.Height > 300);
        }

        await SaveScreenshotAsync(fixture, "native-application-window.png", whole);
        var invalid = await Call(client,
            "native-screenshot",
            ("nodeId", editor.NodeId),
            ("x", -1),
            ("y", 0),
            ("width", 16),
            ("height", 8));
        Assert.True(invalid.IsError == true);
        Assert.Contains("invalid_coordinates", Text(invalid));
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real Windows common item dialog is required.")]
    public async Task Stdio_native_screenshot_captures_the_real_file_picker_and_rejects_its_closed_id()
    {
        await using var fixture = await SampleProcess.StartAsync();
        await using var client = await CreateClientAsync(fixture.PipeName);
        Read<ItemSelectionResult>(await Call(client,
            "ui-select-item",
            ("nodeId", await FindManaged(client, "MainTabs")),
            ("index", 1)));
        var picker = await OpenPicker(client, fixture.ProcessId, "OpenSingleButton", "Automation sample - Open single");
        var image = await Call(client, "native-screenshot", ("nodeId", picker.Info.DialogId));
        using (var bitmap = DecodeScreenshot(image))
        {
            Assert.True(bitmap.Width > 300);
            Assert.True(bitmap.Height > 200);
            Assert.True(bitmap.Pixels.Distinct().Take(8).Count() >= 8,
                "The common dialog contains actual controls, rather than a uniform placeholder.");
        }

        await SaveScreenshotAsync(fixture, "native-common-item-dialog.png", image);
        var region = await Call(client,
            "native-screenshot",
            ("nodeId", picker.Info.PathInputNodeId),
            ("x", 4),
            ("y", 4),
            ("width", 20),
            ("height", 8));
        using (var bitmap = DecodeScreenshot(region))
        {
            Assert.Equal(20, bitmap.Width);
            Assert.Equal(8, bitmap.Height);
        }

        await SaveScreenshotAsync(fixture, "native-common-item-filename-region.png", region);
        await CancelAndClose(client, picker);
        var stale = await Call(client, "native-screenshot", ("nodeId", picker.Info.DialogId));
        Assert.True(stale.IsError == true);
        Assert.True(Text(stale).Contains("stale_node", StringComparison.Ordinal) ||
                    Text(stale).Contains("out_of_scope", StringComparison.Ordinal),
            Text(stale));
    }

    private static SKBitmap DecodeScreenshot(CallToolResult response)
    {
        Assert.False(response.IsError == true, Text(response));
        var image = Assert.Single(response.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        return SKBitmap.Decode(image.DecodedData.ToArray()) ??
               throw new InvalidOperationException("The returned image is not a decodable PNG.");
    }

    private static Task SaveScreenshotAsync(SampleProcess fixture, string filename, CallToolResult response)
    {
        var directory = Path.GetFullPath(Path.Combine(Metadata("PreviewRepositoryPath"),
            "artifacts",
            "verification",
            "native-screenshots",
            Path.GetFileName(fixture.DirectoryPath)));
        Directory.CreateDirectory(directory);
        return File.WriteAllBytesAsync(Path.Combine(directory, filename),
            Assert.Single(response.Content.OfType<ImageContentBlock>()).DecodedData.ToArray(),
            Token);
    }
}
