using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class RecordingMcpTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(IsWindows), Skip = "The recording tool requires Windows.")]
    public async Task Stdio_publishes_recording_controls_and_rejects_start_without_an_attached_application()
    {
        await using var client = await CreateClientAsync();
        var tools = await client.ListToolsAsync(cancellationToken: Token);
        foreach (var name in new[] { "recording-start", "recording-status", "recording-stop", "recording-list", "recording-export" })
            Assert.Single(tools, tool => tool.Name == name);
        var start = Assert.Single(tools, tool => tool.Name == "recording-start").ProtocolTool;
        Assert.False(start.Annotations?.ReadOnlyHint);
        Assert.False(start.Annotations?.DestructiveHint);
        Assert.NotEmpty(start.Icons!);
        var properties = start.InputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("nodeIds", out _));
        Assert.True(properties.TryGetProperty("outputDirectory", out _));
        Assert.True(properties.TryGetProperty("mouseClicks", out _));

        var result = await Call(client, "recording-start");

        Assert.True(result.IsError, Text(result));
        Assert.Contains("not_connected", Text(result));
        Assert.Empty(result.Content.OfType<ImageContentBlock>());
        Assert.Empty(result.Content.OfType<EmbeddedResourceBlock>());
    }

    [AvaloniaFact(SkipUnless = nameof(IsWindows), Skip = "The recording tool requires Windows.")]
    public async Task Stdio_recording_validates_output_directory_and_capture_budgets_before_target_or_GPU_access()
    {
        await using var client = await CreateClientAsync();
        foreach (var arguments in new (string Code, (string Name, object? Value)[] Values)[]
                 {
                     ("invalid_path", [("outputDirectory", "relative-recordings")]),
                     ("invalid_path", [("outputDirectory", "")]),
                     ("invalid_argument", [("frameRate", 0)]),
                     ("invalid_argument", [("maxDurationSeconds", 0)]),
                     ("invalid_argument", [("maxBytes", 0)])
                 })
        {
            var result = await Call(client, "recording-start", arguments.Values);
            Assert.True(result.IsError, Text(result));
            Assert.True(Text(result).Contains(arguments.Code, StringComparison.Ordinal),
                $"Arguments: {string.Join(", ", arguments.Values.Select(value => value.Name))}; {Text(result)}");
        }
    }

    [AvaloniaFact(SkipUnless = nameof(IsWindows), Skip = "The recording tool requires Windows.")]
    public async Task Stdio_recording_maps_control_ids_before_reaching_the_native_capture_boundary()
    {
        var target = new Border { Name = "RecordingRegionSubject", Width = 90, Height = 60 };
        var window = new Window { Width = 300, Height = 200, Content = target };
        window.Show();
        try
        {
            window.UpdateLayout();
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            var id = await Find(client, target.Name!);

            // Headless has no HWND. Getting this exact error proves the gateway translated the
            // public search ID and reached region validation, without starting a GPU recorder.
            var result = await Call(client, "recording-start", ("nodeIds", new[] { id }));

            Assert.True(result.IsError, Text(result));
            Assert.Contains("unsupported_capture_window", Text(result));

            var conflicting = await Call(client, "recording-start", ("nodeIds", new[] { id }), ("windowHandle", 1L));
            Assert.True(conflicting.IsError, Text(conflicting));
            Assert.Contains("invalid_argument", Text(conflicting));

            var stale = await Call(client, "recording-start", ("nodeIds", new[] { long.MaxValue }));
            Assert.True(stale.IsError, Text(stale));
            Assert.Contains("stale_node", Text(stale));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(SkipUnless = nameof(IsWindows), Skip = "The recording tool requires Windows.")]
    public async Task Stdio_recording_rejects_controls_from_distinct_top_levels_before_creating_a_file()
    {
        var first = new Border { Name = "RecordingWindowOne", Width = 60, Height = 40 };
        var second = new Border { Name = "RecordingWindowTwo", Width = 60, Height = 40 };
        var one = new Window { Width = 200, Height = 160, Content = first };
        var two = new Window { Width = 200, Height = 160, Content = second };
        one.Show();
        two.Show();
        try
        {
            one.UpdateLayout();
            two.UpdateLayout();
            await using var agent = Attach(one, two);
            await using var client = await CreateClientAsync(agent.PipeName);
            var firstId = await Find(client, first.Name!);
            var secondId = await Find(client, second.Name!);

            var result = await Call(client, "recording-start", ("nodeIds", new[] { firstId, secondId }));

            Assert.True(result.IsError, Text(result));
            Assert.Contains("recording_multiple_windows", Text(result));
        }
        finally
        {
            two.Close();
            one.Close();
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DiagnosticsAgent Attach(params Window[] windows) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions
        {
            PipeName = "recording-mcp-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => windows
        });

    private static Task<McpClient> CreateClientAsync(string? pipe = null)
    {
        var path = typeof(RecordingMcpTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(item => item.Key == "PreviewCliPath").Value!;
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "Recording MCP contract tests",
            Arguments = pipe is null ? [path, "mcp"] : [path, "mcp", "--pipe", pipe],
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        }), cancellationToken: Token);
    }

    private static ValueTask<CallToolResult> Call(McpClient client, string tool,
        params (string Name, object? Value)[] values) => client.CallToolAsync(tool,
        values.ToDictionary(value => value.Name, value => value.Value), cancellationToken: Token);

    private static async Task<long> Find(McpClient client, string name)
    {
        var result = await Call(client, "search", ("query", name));
        Assert.False(result.IsError == true, Text(result));
        var root = result.StructuredContent is { } content
            ? JsonNode.Parse(JsonSerializer.Serialize(content))
            : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        var matches = root!.Deserialize<TreeSearchResult>(ProtocolJson.Options)!;
        return Assert.Single(matches.Matches, item => item.Name == name).Id;
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}
