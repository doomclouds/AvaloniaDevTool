using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class AgentScreenshotMcpTests
{
    [AvaloniaFact]
    public async Task Stdio_screenshot_crops_real_subtree_pixels_and_reports_actual_scale_and_bounds()
    {
        var left = new Border { Width = 40, Height = 60, Background = Brushes.Red };
        var right = new Border { Width = 80, Height = 60, Background = Brushes.Blue };
        Canvas.SetLeft(right, 40);
        var target = new Canvas { Name = "RegionScreenshot", Width = 120, Height = 60, Children = { left, right } };
        var outer = new Border { Padding = new Thickness(30), Child = target };
        var window = new Window { Width = 220, Height = 160, Content = outer };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            var declaration = Assert.Single(await client.ListToolsAsync(), tool => tool.Name == "ui-screenshot")
                .ProtocolTool;
            Assert.True(declaration.Annotations?.ReadOnlyHint);
            Assert.False(declaration.Annotations?.DestructiveHint);
            var id = await Find(client, target.Name!);

            var full = await Call(client, "ui-screenshot", ("nodeId", id));
            using (var bitmap = Decode(full))
            {
                Assert.Equal(120, bitmap.Width);
                Assert.Equal(60, bitmap.Height);
                Assert.Equal(SKColors.Red, bitmap.GetPixel(20, 30));
                Assert.Equal(SKColors.Blue, bitmap.GetPixel(80, 30));
            }

            var metadata = Metadata(full);
            Assert.Equal(id, metadata.GetProperty("NodeId").GetInt64());
            Assert.Equal(120, metadata.GetProperty("Width").GetInt32());
            Assert.Equal(60, metadata.GetProperty("Height").GetInt32());
            Assert.Equal(1, metadata.GetProperty("Scale").GetDouble());
            Assert.Equal(96, metadata.GetProperty("DpiX").GetDouble());
            Assert.Equal(new ScreenshotBounds(0, 0, 120, 60),
                metadata.GetProperty("Bounds").Deserialize<ScreenshotBounds>());

            foreach (var scale in new[] { 0.5, 1, 2 })
            {
                var result = await Call(client,
                    "ui-screenshot",
                    ("nodeId", id),
                    ("x", 50),
                    ("y", 10),
                    ("width", 30),
                    ("height", 20),
                    ("scale", scale));
                using var bitmap = Decode(result);
                Assert.Equal((int)(30 * scale), bitmap.Width);
                Assert.Equal((int)(20 * scale), bitmap.Height);
                Assert.Equal(SKColors.Blue, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2));
                Assert.Equal(SKColors.Blue, bitmap.GetPixel(0, 0));
                metadata = Metadata(result);
                Assert.Equal(bitmap.Width, metadata.GetProperty("Width").GetInt32());
                Assert.Equal(bitmap.Height, metadata.GetProperty("Height").GetInt32());
                Assert.Equal(scale, metadata.GetProperty("Scale").GetDouble());
                Assert.Equal(96 * scale, metadata.GetProperty("DpiX").GetDouble());
                Assert.Equal(96 * scale, metadata.GetProperty("DpiY").GetDouble());
                Assert.Equal(new ScreenshotBounds(50, 10, 30, 20),
                    metadata.GetProperty("Bounds").Deserialize<ScreenshotBounds>());
            }

            var fractional = await Call(client,
                "ui-screenshot",
                ("nodeId", id),
                ("x", 55.25),
                ("y", 10.5),
                ("width", 11.25),
                ("height", 7.5),
                ("scale", 1.5));
            using (var bitmap = Decode(fractional))
            {
                Assert.Equal(17, bitmap.Width);
                Assert.Equal(12, bitmap.Height);
                Assert.Equal(SKColors.Blue, bitmap.GetPixel(8, 5));
            }

            right.Background = Brushes.Green;
            var updated = await Call(client,
                "ui-screenshot",
                ("nodeId", id),
                ("x", 50),
                ("y", 10),
                ("width", 30),
                ("height", 20));
            using (var bitmap = Decode(updated))
                Assert.Equal(SKColors.Green, bitmap.GetPixel(15, 10));

            var official = await Call(client, "screenshot", ("nodeId", id));
            Assert.Single(official.Content);
            using (var bitmap = Decode(official))
            {
                Assert.Equal(120, bitmap.Width);
                Assert.Equal(60, bitmap.Height);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Stdio_screenshot_rejects_invalid_regions_budgets_domains_and_invisible_or_detached_controls()
    {
        var target = new Border { Name = "GuardedScreenshot", Width = 120, Height = 60, Background = Brushes.Red };
        target.Resources["ScreenshotProbeResource"] = Brushes.Blue;
        var outer = new Border { Child = target };
        var empty = new Border { Name = "EmptyRegionScreenshot", Width = 0, Height = 0 };
        var window = new Window { Width = 200, Height = 200, Content = new StackPanel { Children = { outer, empty } } };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            var id = await Find(client, target.Name!);
            var resources =
                Read<OfficialResourceSnapshot>(await Call(client, "resources", ("nodeId", id), ("onlySelf", true)));
            var dictionaryId = Assert.Single(resources.Entries, item => item.StringKey == "ScreenshotProbeResource")
                .OwnerDictionaryId;
            await Error(client, "unsupported_object", ("nodeId", dictionaryId));
            await Error(client, "invalid_bounds", ("nodeId", id), ("x", 0));
            foreach (var (x, y, width, height) in new[]
                     {
                         (-1d, 0d, 10d, 10d), (0d, -1d, 10d, 10d), (0d, 0d, 0d, 10d),
                         (0d, 0d, 10d, -1d), (110d, 0d, 11d, 10d), (0d, 55d, 10d, 6d),
                         (double.MaxValue, 0d, 10d, 10d), (0d, 0d, double.MaxValue, 10d)
                     })
                await Error(client,
                    "invalid_bounds",
                    ("nodeId", id),
                    ("x", x),
                    ("y", y),
                    ("width", width),
                    ("height", height));
            foreach (var scale in new[] { 0d, -1d, double.MaxValue, double.Epsilon })
                await Error(client, "invalid_scale", ("nodeId", id), ("scale", scale));
            await Error(client, "screenshot_limit", ("nodeId", id), ("scale", 18));
            // Each edge fits, but 1800 x 1200 exceeds the total pixel budget.
            await Error(client,
                "screenshot_limit",
                ("nodeId", id),
                ("width", 90),
                ("height", 60),
                ("x", 0),
                ("y", 0),
                ("scale", 20));
            await Error(client, "stale_node", ("nodeId", 1));
            await Error(client, "stale_node", ("nodeId", -1));
            var nativeDomain = await Call(client, "ui-screenshot", ("nodeId", "native:window:1"));
            Assert.True(nativeDomain.IsError);
            Assert.Empty(nativeDomain.Content.OfType<ImageContentBlock>());
            await Error(client, "invalid_bounds", ("nodeId", await Find(client, empty.Name!)));

            outer.IsVisible = false;
            await Error(client, "hidden_target", ("nodeId", id));
            outer.IsVisible = true;
            outer.Child = null;
            await Error(client, "object_detached", ("nodeId", id));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Stdio_screenshot_rejects_png_over_the_encoded_byte_budget()
    {
        using var noise = new SKBitmap(1024, 512);
        var random = new Random(149);
        var pixels = new SKColor[noise.Width * noise.Height];
        for (var index = 0; index < pixels.Length; index++)
            pixels[index] = new SKColor((uint)random.Next(0x1000000) | 0xff000000);
        noise.Pixels = pixels;
        using var encoded = noise.Encode(SKEncodedImageFormat.Png, 100);
        using var source = new Bitmap(encoded.AsStream());
        var target = new Image { Name = "NoiseScreenshot", Width = 1024, Height = 512, Source = source };
        var window = new Window { Width = 1100, Height = 600, Content = target };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            await Error(client, "screenshot_limit", ("nodeId", await Find(client, target.Name!)));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Stdio_screenshot_renders_a_small_region_of_a_large_control_and_reports_unsupported_rendering()
    {
        var large = new Border
            { Name = "LargeRegionScreenshot", Width = 8000, Height = 80, Background = Brushes.Magenta };
        var unsupported = new UnsupportedCaptureControl { Name = "UnsupportedScreenshot", Width = 40, Height = 40 };
        Canvas.SetTop(unsupported, 100);
        var window = new Window
            { Width = 300, Height = 220, Content = new Canvas { Children = { large, unsupported } } };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            var id = await Find(client, large.Name!);
            await Error(client, "screenshot_limit", ("nodeId", id));
            var region = await Call(client,
                "ui-screenshot",
                ("nodeId", id),
                ("x", 7900),
                ("y", 20),
                ("width", 60),
                ("height", 40));
            using (var bitmap = Decode(region))
            {
                Assert.Equal(60, bitmap.Width);
                Assert.Equal(40, bitmap.Height);
                Assert.Equal(SKColors.Magenta, bitmap.GetPixel(30, 20));
            }

            unsupported.RejectCapture = true;
            await Error(client, "unsupported_renderer", ("nodeId", await Find(client, unsupported.Name!)));
        }
        finally
        {
            window.Close();
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public async Task Direct_screenshot_rejects_nonfinite_arguments_before_json_serialization(double value)
    {
        await using var session = new ToolSession(null);
        var tools = new AgentTools(session, new ManagedToolContext(session));
        var scaleError = await Assert.ThrowsAsync<McpException>(() =>
            tools.UiScreenshot(1, scale: value, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("invalid_scale", scaleError.Message);
        var regionError = await Assert.ThrowsAsync<McpException>(() =>
            tools.UiScreenshot(1, value, 0, 1, 1, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("invalid_bounds", regionError.Message);
    }

    private static DiagnosticsAgent Attach(Window window) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions
            { PipeName = "agent-screenshot-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });

    private static Task<McpClient> CreateClientAsync(string pipe)
    {
        var path = typeof(AgentScreenshotMcpTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(item => item.Key == "PreviewCliPath").Value!;
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet", Name = "Agent screenshot integration", Arguments = [path, "mcp", "--pipe", pipe],
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            }),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static ValueTask<CallToolResult> Call(McpClient client,
        string tool,
        params (string Name, object? Value)[] values) =>
        client.CallToolAsync(tool,
            values.ToDictionary(value => value.Name, value => value.Value),
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task Error(McpClient client, string code, params (string Name, object? Value)[] values)
    {
        var result = await Call(client, "ui-screenshot", values);
        Assert.True(result.IsError, Text(result));
        Assert.Contains(code, Text(result));
        Assert.Empty(result.Content.OfType<ImageContentBlock>());
    }

    private static async Task<long> Find(McpClient client, string name)
    {
        var result = await Call(client, "search", ("query", name));
        Assert.NotEqual(true, result.IsError);
        return Assert.Single(Read<TreeSearchResult>(result).Matches, item => item.Name == name).Id;
    }

    private static SKBitmap Decode(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        return SKBitmap.Decode(image.DecodedData.ToArray());
    }

    private static JsonElement Metadata(CallToolResult result)
    {
        using var document = JsonDocument.Parse(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
        return document.RootElement.Clone();
    }

    private static T Read<T>(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var root = result.StructuredContent is { } content
            ? JsonNode.Parse(JsonSerializer.Serialize(content))
            : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text));

    private sealed class UnsupportedCaptureControl : Control
    {
        public bool RejectCapture { get; set; }

        public override void Render(DrawingContext context)
        {
            if (RejectCapture)
                throw new NotSupportedException("The control's custom rendering is unavailable.");
            context.FillRectangle(Brushes.Green, new Rect(Bounds.Size));
        }
    }
}
