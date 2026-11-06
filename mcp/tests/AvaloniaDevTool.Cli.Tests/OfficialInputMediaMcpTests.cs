using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class OfficialInputMediaMcpTests
{
    [AvaloniaFact]
    public async Task Official_input_routes_all_four_events_and_reports_focus_disabled_and_invalid_arguments()
    {
        var text = new TextBox { Name = "OfficialInput", Width = 200, Height = 40 };
        var button = new Button { Name = "OfficialButton", Width = 150, Height = 40, Content = "Click" };
        var unfocusable = new Border { Name = "NoFocus", Width = 100, Height = 20 };
        var clicks = 0;
        var downs = 0;
        var ups = 0;
        button.Click += (_, _) => clicks++;
        text.AddHandler(InputElement.KeyDownEvent,
            (_, _) => downs++,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            true);
        text.AddHandler(InputElement.KeyUpEvent,
            (_, _) => ups++,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
            true);
        var window = new Window
            { Width = 300, Height = 250, Content = new StackPanel { Children = { text, button, unfocusable } } };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            var declaration = Assert.Single(await client.ListToolsAsync(), item => item.Name == "input").ProtocolTool;
            Assert.Equal(new[] { "Click", "KeyDown", "KeyUp", "Text" },
                declaration.InputSchema.GetProperty("properties")
                    .GetProperty("inputEvent").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
            var id = await Find(client, "OfficialInput");
            Assert.True(Read<InputResult>(await Call(client,
                "input",
                ("nodeId", id),
                ("inputEvent", "Text"),
                ("arg", "abc中文"))).Delivered);
            Assert.Equal("abc中文", text.Text);
            Assert.True(text.IsFocused);
            Assert.True(Read<InputResult>(await Call(client,
                "input",
                ("nodeId", id),
                ("inputEvent", "KeyDown"),
                ("arg", "Back"))).Delivered);
            Assert.Equal("abc中", text.Text);
            Assert.True(downs > 0);
            Assert.Equal(0, ups);
            Assert.True(Read<InputResult>(await Call(client,
                "input",
                ("nodeId", id),
                ("inputEvent", "KeyUp"),
                ("arg", "Back"))).Delivered);
            Assert.True(ups > 0);
            Assert.Equal("abc中", text.Text);
            var buttonId = await Find(client, "OfficialButton");
            Assert.True(Read<InputResult>(await Call(client, "input", ("nodeId", buttonId), ("inputEvent", "Click")))
                .Delivered);
            Assert.Equal(1, clicks);
            button.IsEnabled = false;
            Assert.Contains("disabled_target",
                Text(await Error(client, "input", ("nodeId", buttonId), ("inputEvent", "Click"))));
            Assert.Equal(1, clicks);
            Assert.Contains("focus_failed",
                Text(await Error(client,
                    "input",
                    ("nodeId", await Find(client, "NoFocus")),
                    ("inputEvent", "Text"),
                    ("arg", "x"))));
            Assert.Contains("invalid_input",
                Text(await Error(client, "input", ("nodeId", id), ("inputEvent", "KeyDown"), ("arg", "NotAKey"))));
            await Error(client, "input", ("nodeId", id), ("inputEvent", "Text"));
            await Error(client, "input", ("nodeId", id), ("inputEvent", "Text"), ("arg", null));
            await Error(client, "input", ("nodeId", id), ("inputEvent", "PointerDown"));
            await Error(client, "input", ("nodeId", id), ("inputEvent", 2));
            button.IsEnabled = true;
            await Error(client, "input", ("nodeId", buttonId), ("inputEvent", 0));
            Assert.Equal(1, clicks);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Official_resources_use_real_application_node_ancestor_merged_and_theme_scopes()
    {
        var application = Application.Current!;
        const string key = "OfficialScopeDuplicate";
        application.Resources[key] = "application";
        var child = new Border { Name = "Application.Resources", Width = 40, Height = 40 };
        child.Resources[key] = "child";
        child.Resources.MergedDictionaries.Add(new ResourceDictionary { ["OfficialMerged"] = "merged" });
        child.Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary { ["OfficialTheme"] = "dark" };
        var parent = new Border { Name = "ParentResourceScope", Child = child };
        parent.Resources[key] = "parent";
        var window = new Window { Content = parent };
        window.Show();
        try
        {
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            var id = await Find(client, "Application.Resources");
            var app = Read<ResourceInspectionResult>(await Call(client, "resources"));
            var zero = Read<ResourceInspectionResult>(await Call(client, "resources", ("nodeId", 0)));
            Assert.Equal(0, app.NodeId);
            Assert.Contains(app.Entries, entry => entry.StringKey == key && entry.Value == "application");
            Assert.Equal(app.Entries.Where(entry => entry.StringKey == key),
                zero.Entries.Where(entry => entry.StringKey == key));
            Assert.DoesNotContain(app.Entries, entry => entry.StringKey == key && entry.Value == "child");
            var all = Read<ResourceInspectionResult>(await Call(client, "resources", ("nodeId", id)));
            Assert.Equal(new[] { "child", "parent", "application" },
                all.Entries.Where(entry => entry.StringKey == key).Select(entry => entry.Value));
            var local = Read<ResourceInspectionResult>(await Call(client,
                "resources",
                ("nodeId", id),
                ("onlySelf", true)));
            Assert.Equal(id, local.NodeId);
            Assert.True(local.OnlySelf);
            Assert.Equal("child", Assert.Single(local.Entries, entry => entry.StringKey == key).Value);
            Assert.DoesNotContain(local.Tree.Roots, provider => provider.Scope == "Application");
            Assert.Contains(local.Entries, entry => entry.StringKey == "OfficialMerged" && entry.Value == "merged");
            Assert.Contains(local.Entries,
                entry => entry.StringKey == "OfficialTheme" && entry.Theme == "Dark" && entry.Value == "dark");
            Assert.All(local.Entries, entry => Assert.True(entry.OwnerDictionaryId > uint.MaxValue));
            await Error(client, "resources", ("nodeId", -1));
        }
        finally
        {
            window.Close();
            application.Resources.Remove(key);
        }
    }

    [AvaloniaFact]
    public async Task Official_resources_report_entry_and_text_limits_without_claiming_complete_results()
    {
        var target = new Border { Name = "BoundedResources" };
        for (var index = 0; index < 1100; index++)
            target.Resources["Key" + index.ToString("D4")] = index;
        target.Resources["AClippedValue"] = new string('x', 2048);
        target.Resources["A" + new string('k', 300)] = "long key";
        var window = new Window { Content = target };
        window.Show();
        try
        {
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            var result = Read<ResourceInspectionResult>(await Call(client,
                "resources",
                ("nodeId", await Find(client, "BoundedResources")),
                ("onlySelf", true)));
            Assert.True(result.Truncated);
            Assert.Equal(1000, result.Entries.Count);
            Assert.Contains(result.Limitations, note => note.Contains("1000", StringComparison.Ordinal));
            var value = Assert.Single(result.Entries, entry => entry.StringKey == "AClippedValue");
            Assert.True(value.ValueTruncated);
            Assert.True(value.Value!.Length <= 1025);
            Assert.Contains(result.Entries, entry => entry.KeyTruncated && entry.StringKey is null);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Official_assets_return_full_verified_text_image_font_and_binary_resources()
    {
        _ = Assembly.Load("Avalonia.Fonts.Inter");
        var fontSource = AssetLoader.GetAssets(new Uri("avares://Avalonia.Fonts.Inter/"), null)
            .Where(uri => uri.AbsolutePath.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(uri => uri.AbsoluteUri, StringComparer.Ordinal).First();
        var window = new Window();
        window.Show();
        try
        {
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            var assets = Read<AssetInfo[]>(await Call(client, "assets"));
            var text = Assert.Single(assets,
                item => item.Uri.Contains("AvaloniaDevTool.Cli.Tests/Assets/asset-long-utf8.txt",
                    StringComparison.OrdinalIgnoreCase));
            var image = Assert.Single(assets,
                item => item.Uri.Contains("AvaloniaDevTool.Cli.Tests/Assets/official-pixel.png",
                    StringComparison.OrdinalIgnoreCase));
            var binary = Assert.Single(assets,
                item => item.Uri.Contains("AvaloniaDevTool.Cli.Tests/Assets/official-binary.bin",
                    StringComparison.OrdinalIgnoreCase));
            var font = assets.First(item => item.AssemblyName == "Avalonia.Fonts.Inter" && item.Extension == ".ttf");
            foreach (var (asset, mime, sourceUri) in new[]
                     {
                         (text, "text/plain", new Uri("avares://AvaloniaDevTool.Cli.Tests/Assets/asset-long-utf8.txt")),
                         (image, "image/png", new Uri("avares://AvaloniaDevTool.Cli.Tests/Assets/official-pixel.png")),
                         (font, "font/ttf", fontSource),
                         (binary, "application/octet-stream",
                             new Uri("avares://AvaloniaDevTool.Cli.Tests/Assets/official-binary.bin"))
                     })
            {
                var opened = await Call(client, "open-asset", ("assetUrl", asset.Uri));
                Assert.NotEqual(true, opened.IsError);
                Assert.Empty(opened.Content.OfType<ImageContentBlock>());
                var embedded = Assert.Single(opened.Content.OfType<EmbeddedResourceBlock>());
                var blob = Assert.IsType<BlobResourceContents>(embedded.Resource);
                Assert.Equal(asset.Uri, blob.Uri);
                Assert.Equal(mime, blob.MimeType);
                var actual = blob.DecodedData.ToArray();
                using var source = AssetLoader.Open(sourceUri);
                using var expected = new MemoryStream();
                source.CopyTo(expected);
                Assert.Equal(expected.ToArray(), actual);
                if (asset == image)
                {
                    using var bitmap = SKBitmap.Decode(actual);
                    Assert.Equal(2, bitmap.Width);
                    Assert.Equal(2, bitmap.Height);
                    Assert.Equal(SKColors.Red, bitmap.GetPixel(0, 0));
                }

                using var metadata = JsonDocument.Parse(Text(opened));
                Assert.Equal(actual.Length, metadata.RootElement.GetProperty("ByteLength").GetInt64());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(actual)).ToLowerInvariant(),
                    metadata.RootElement.GetProperty("Sha256").GetString());
                Assert.True(metadata.RootElement.GetProperty("Complete").GetBoolean());
                if (asset == text)
                    Assert.True(actual.Length > 1024 * 1024);
            }

            Assert.Contains("invalid_asset_uri",
                Text(await Error(client, "open-asset", ("assetUrl", "file:///C:/Windows/win.ini"))));
            Assert.Contains("invalid_asset_uri",
                Text(await Error(client, "open-asset", ("assetUrl", "avares://NotLoaded/Missing.bin"))));
            await Error(client, "open-asset", ("assetUrl", text.Uri + "?query=1"));
            await Error(client, "open-asset", ("assetUrl", text.Uri + "#fragment"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Official_screenshot_returns_target_png_dimensions_and_pixels_and_explicit_limits()
    {
        var red = new Border { Name = "RedCapture", Width = 120, Height = 64, Background = Brushes.Red };
        var blue = new Border { Name = "BlueCapture", Width = 80, Height = 40, Background = Brushes.Blue };
        var empty = new Border { Name = "EmptyCapture", Width = 0, Height = 0 };
        var wide = new Border { Name = "WideCapture", Width = 2049, Height = 1 };
        var window = new Window
            { Width = 400, Height = 300, Content = new StackPanel { Children = { red, blue, empty, wide } } };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            foreach (var (name, width, height, color) in new[]
                         { ("RedCapture", 120, 64, SKColors.Red), ("BlueCapture", 80, 40, SKColors.Blue) })
            {
                var result = await Call(client, "screenshot", ("nodeId", await Find(client, name)));
                Assert.NotEqual(true, result.IsError);
                var image = Assert.Single(result.Content.OfType<ImageContentBlock>());
                Assert.Equal("image/png", image.MimeType);
                using var bitmap = SKBitmap.Decode(image.DecodedData.ToArray());
                Assert.Equal(width, bitmap.Width);
                Assert.Equal(height, bitmap.Height);
                Assert.Equal(color, bitmap.GetPixel(width / 2, height / 2));
            }

            Assert.Contains("invalid_bounds",
                Text(await Error(client, "screenshot", ("nodeId", await Find(client, "EmptyCapture")))));
            Assert.Contains("screenshot_limit",
                Text(await Error(client, "screenshot", ("nodeId", await Find(client, "WideCapture")))));
            await Error(client, "screenshot", ("nodeId", 0));
        }
        finally
        {
            window.Close();
        }
    }

    private static DiagnosticsAgent Attach(Window window) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions
            { PipeName = "official-input-media-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });

    private static Task<McpClient> CreateClientAsync(string pipe)
    {
        var assembly = typeof(OfficialInputMediaMcpTests).Assembly.Location;
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "Official input media integration",
            Arguments =
            [
                "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"), "--depsfile",
                Path.ChangeExtension(assembly, ".deps.json"), typeof(Program).Assembly.Location, "mcp", "--pipe", pipe
            ],
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        }));
    }

    private static ValueTask<CallToolResult> Call(McpClient client,
        string name,
        params (string Name, object? Value)[] values) =>
        client.CallToolAsync(name,
            values.ToDictionary(value => value.Name, value => value.Value),
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<CallToolResult> Error(McpClient client,
        string name,
        params (string Name, object? Value)[] values)
    {
        var result = await Call(client, name, values);
        Assert.True(result.IsError);
        return result;
    }

    private static async Task<long> Find(McpClient client, string name) => Assert.Single(
        Read<TreeSearchResult>(await Call(client, "search", ("query", name))).Matches,
        item => item.Name == name).Id;

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

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
}
