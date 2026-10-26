using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class BreakpointSourceMcpTests
{
    [AvaloniaFact]
    public async Task Stdio_source_and_receiver_breakpoints_roundtrip_update_disable_count_and_remove_on_a_real_agent()
    {
        var first = new Button { Name = "BreakpointFirst" };
        var second = new Button { Name = "BreakpointSecond" };
        var parent = new StackPanel { Name = "BreakpointReceiver", Children = { first, second } };
        var target = new Window { Content = parent };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                {
                    PipeName = "breakpoint-source-mcp-" + Guid.NewGuid().ToString("N"),
                    TopLevelProvider = () => [target]
                });
            await using var client = await CreateClientAsync();
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            var create = Assert.Single(tools, item => item.Name == "breakpoint-create");
            Assert.Contains("sourceObjectId", create.JsonSchema.ToString());
            Read<SessionInfo>(await client.CallToolAsync("attach-to-app",
                new Dictionary<string, object?> { ["pipeName"] = agent.PipeName }));

            async Task<long> Find(string name) => Assert.Single(Read<TreeSearchResult>(await client.CallToolAsync(
                "search",
                new Dictionary<string, object?> { ["query"] = name, ["kind"] = "Logical" })).Matches).Id;

            var firstId = await Find(first.Name!);
            var secondId = await Find(second.Name!);
            var parentId = await Find(parent.Name!);
            var definition = Read<BreakpointInfo>(await client.CallToolAsync("breakpoint-create",
                new Dictionary<string, object?>
                {
                    ["kind"] = "Event", ["memberId"] = "Avalonia.Controls.Button:Click", ["objectId"] = parentId,
                    ["sourceObjectId"] = firstId, ["comparison"] = "Equal", ["compareTo"] = 2, ["logMessage"] = false
                }));
            Assert.Equal(firstId, definition.Definition.SourceObjectId);
            Assert.Equal(parentId, definition.Definition.ObjectId);
            parent.AddHandler(Button.ClickEvent, (_, args) => args.Handled = true, RoutingStrategies.Bubble, true);
            second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var reused = new RoutedEventArgs(Button.ClickEvent);
            first.RaiseEvent(reused);
            first.RaiseEvent(reused);
            var history = Read<BreakpointHistory>(await client.CallToolAsync("breakpoints",
                new Dictionary<string, object?> { ["history"] = true }));
            Assert.Equal(2, history.Hits.Count);
            Assert.Equal([false, true], history.Hits.Select(hit => hit.ConditionMet));
            Assert.All(history.Hits,
                hit =>
                {
                    Assert.Equal(firstId, hit.ObjectId);
                    Assert.Contains("handled=True", hit.Summary);
                });
            var updated = definition.Definition with
            {
                SourceObjectId = secondId, Enabled = false, RemoveOnHit = true, CompareTo = 1
            };
            var disabled = Read<BreakpointInfo>(await client.CallToolAsync("breakpoint-update",
                new Dictionary<string, object?>
                    { ["id"] = definition.Id, ["definition"] = updated, ["resetHitCount"] = true }));
            Assert.Equal(BreakpointState.Disabled, disabled.State);
            Assert.Equal(0, disabled.HitCount);
            second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var listed =
                Read<BreakpointInfo[]>(await client.CallToolAsync("breakpoints", new Dictionary<string, object?>()));
            Assert.Equal(0, Assert.Single(listed).HitCount);
            Read<BreakpointInfo>(await client.CallToolAsync("breakpoint-update",
                new Dictionary<string, object?>
                    { ["id"] = definition.Id, ["definition"] = updated with { Enabled = true } }));
            first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(
                Read<BreakpointInfo[]>(await client.CallToolAsync("breakpoints", new Dictionary<string, object?>())));
            history = Read<BreakpointHistory>(await client.CallToolAsync("breakpoints",
                new Dictionary<string, object?> { ["history"] = true }));
            Assert.Equal(3, history.Hits.Count);
            Assert.Equal(secondId, history.Hits[^1].ObjectId);
            Assert.True(history.Hits[^1].ConditionMet);
            var invalid = await client.CallToolAsync("breakpoint-create",
                new Dictionary<string, object?>
                {
                    ["kind"] = "Property", ["memberId"] = "Avalonia.Controls.TextBlock:Text",
                    ["sourceObjectId"] = firstId
                });
            Assert.True(invalid.IsError);
            Assert.Empty(
                Read<BreakpointInfo[]>(await client.CallToolAsync("breakpoints", new Dictionary<string, object?>())));
        }
        finally
        {
            target.Close();
        }
    }

    private static Task<McpClient> CreateClientAsync()
    {
        var assembly = typeof(BreakpointSourceMcpTests).Assembly.Location;
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "Breakpoint source MCP integration",
            Arguments =
            [
                "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"), "--depsfile",
                Path.ChangeExtension(assembly, ".deps.json"), typeof(Program).Assembly.Location, "mcp", "--profile",
                "extended"
            ],
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        }));
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text));

    private static T Read<T>(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var root = result.StructuredContent is { } structured
            ? JsonNode.Parse(JsonSerializer.Serialize(structured))
            : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }
}
