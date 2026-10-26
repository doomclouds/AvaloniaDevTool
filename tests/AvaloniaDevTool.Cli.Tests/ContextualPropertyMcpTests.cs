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

public sealed class ContextualPropertyMcpTests
{
    [AvaloniaFact]
    public async Task Stdio_props_context_filter_and_explicit_CLR_opt_in_preserve_legacy_defaults()
    {
        var probe = new Probe { Name = "McpContextTarget" };
        Grid.SetRow(probe, 2);
        var window = new Window { Content = new StackPanel { Children = { probe } } };
        window.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                    { PipeName = "context-mcp-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            var assembly = typeof(ContextualPropertyMcpTests).Assembly.Location;
            await using var client = await McpClient.CreateAsync(new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = "dotnet", Name = "Contextual properties integration",
                    Arguments =
                    [
                        "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"), "--depsfile",
                        Path.ChangeExtension(assembly, ".deps.json"), typeof(Program).Assembly.Location, "mcp",
                        "--profile", "extended"
                    ],
                    ShutdownTimeout = TimeSpan.FromSeconds(5)
                }));
            Assert.NotEqual(true,
                (await client.CallToolAsync("attach-to-app",
                    new Dictionary<string, object?> { ["pipeName"] = agent.PipeName })).IsError);
            var found = Read<TreeSearchResult>(await client.CallToolAsync("search",
                new Dictionary<string, object?> { ["query"] = probe.Name, ["kind"] = "Logical" }));
            var id = Assert.Single(found.Matches).Id;
            var all = Read<PropertyInfoDto[]>(await client.CallToolAsync("props",
                new Dictionary<string, object?> { ["nodeId"] = id, ["propertyNames"] = new[] { "Row", "LazyClr" } }));
            Assert.Single(all);
            Assert.Equal("Avalonia.Controls.Grid:Row", all[0].Id);
            Assert.False(all[0].Presentation!.Context!.IsApplicable);
            Assert.Equal(0, probe.GetterReads);
            var contextual = Read<PropertyInfoDto[]>(await client.CallToolAsync("props",
                new Dictionary<string, object?>
                    { ["nodeId"] = id, ["propertyNames"] = new[] { "Row", "LazyClr" }, ["contextual"] = true }));
            Assert.Empty(contextual);
            Assert.Equal(0, probe.GetterReads);
            var clr = Read<PropertyInfoDto[]>(await client.CallToolAsync("props",
                new Dictionary<string, object?>
                {
                    ["nodeId"] = id, ["propertyNames"] = new[] { "LazyClr" }, ["includeClr"] = true,
                    ["contextual"] = true
                }));
            Assert.Equal("explicit", Assert.Single(clr).Value);
            Assert.Equal(1, probe.GetterReads);
        }
        finally
        {
            window.Close();
        }
    }

    private static T Read<T>(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var root = result.StructuredContent is { } structured
            ? JsonNode.Parse(JsonSerializer.Serialize(structured))
            : JsonNode.Parse(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text)));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }

    private sealed class Probe : Border
    {
        public int GetterReads { get; private set; }

        public string LazyClr
        {
            get
            {
                GetterReads++;
                return "explicit";
            }
        }
    }
}
