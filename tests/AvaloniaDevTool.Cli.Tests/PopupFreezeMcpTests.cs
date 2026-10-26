using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class PopupFreezeMcpTests
{
    [AvaloniaFact]
    public async Task Stdio_popup_freeze_tools_apply_read_and_resume_real_popup_state()
    {
        var anchor = new Button { Content = "Anchor" };
        var window = new Window { Content = anchor };
        window.Show();
        window.UpdateLayout();
        var popup = new Popup
        {
            PlacementTarget = anchor, IsLightDismissEnabled = true,
            Child = new Border { Width = 80, Height = 50 }
        };
        try
        {
            popup.Open();
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                {
                    PipeName = "popup-freeze-mcp-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window]
                });
            await using var client = await CreateClientAsync();
            var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains(tools, tool => tool.Name == "popup-freeze");
            Assert.Contains(tools, tool => tool.Name == "popup-freeze-resume");
            var set = Assert.Single(tools, tool => tool.Name == "popup-freeze-set");
            Assert.Contains("rootId", set.JsonSchema.ToString());
            Read<SessionInfo>(await client.CallToolAsync("attach-to-app",
                new Dictionary<string, object?> { ["pipeName"] = agent.PipeName }));
            var status =
                Read<PopupFreezeStatusDto>(
                    await client.CallToolAsync("popup-freeze", new Dictionary<string, object?>()));
            var root = Assert.Single(status.Roots);
            var frozen = Read<PopupFreezeStatusDto>(await client.CallToolAsync("popup-freeze-set",
                new Dictionary<string, object?>
                    { ["isFrozen"] = true, ["rootId"] = root.RootId }));
            Assert.True(frozen.IsActive);
            Assert.False(popup.IsLightDismissEnabled);
            Assert.True(Assert.Single(frozen.Popups).HasOverride);
            popup.Close();
            status = Read<PopupFreezeStatusDto>(await client.CallToolAsync("popup-freeze",
                new Dictionary<string, object?>()));
            Assert.Empty(status.Popups);
            Assert.False(popup.IsOpen);
            var resumed =
                Read<PopupFreezeStatusDto>(await client.CallToolAsync("popup-freeze-resume",
                    new Dictionary<string, object?>()));
            Assert.False(resumed.IsActive);
            Assert.True(popup.IsLightDismissEnabled);
            Assert.False(popup.IsOpen);
        }
        finally
        {
            popup.Close();
            window.Close();
        }
    }

    private static Task<McpClient> CreateClientAsync()
    {
        var assembly = typeof(PopupFreezeMcpTests).Assembly.Location;
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "Popup freeze MCP integration",
            Arguments =
            [
                "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"), "--depsfile",
                Path.ChangeExtension(assembly, ".deps.json"), typeof(Program).Assembly.Location, "mcp", "--profile",
                "extended"
            ],
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        }));
    }

    private static T Read<T>(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text));
        var root = result.StructuredContent is { } structured
            ? JsonNode.Parse(JsonSerializer.Serialize(structured))
            : JsonNode.Parse(text);
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }
}
