using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using AvaloniaDevTool.Remote;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class RemoteMcpTests
{
    [AvaloniaFact]
    public async Task Stdio_remote_attach_uses_environment_credentials_and_exports_full_asset()
    {
        var text = new TextBlock { Name = "RemoteMcpTarget", Text = "Original remote text" };
        var window = new Window { Content = text };
        window.Show();
        var outputDirectory = Path.Combine(Path.GetTempPath(), "avdt-remote-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                    { PipeName = "remote-mcp-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            var token = DiagnosticsRemoteServer.GenerateBearerToken();
            var variable = "AVDT_MCP_TOKEN_" + Guid.NewGuid().ToString("N");
            await using var server = await DiagnosticsRemoteServer.StartAsync(agent, new() { BearerToken = token });
            await using var client = await CreateClientAsync(new Dictionary<string, string?> { [variable] = token });
            var listed = await client.ListToolsAsync();
            Assert.Contains(listed, tool => tool.Name == "attach-to-remote");
            Assert.Contains(listed, tool => tool.Name == "export-asset");
            var attached = await client.CallToolAsync("attach-to-remote",
                new Dictionary<string, object?>
                    { ["endpoint"] = server.Endpoint.AbsoluteUri, ["tokenEnvironmentVariable"] = variable });
            var session = Read<SessionInfo>(attached);
            Assert.Equal(Environment.ProcessId, session.ProcessId);
            Assert.DoesNotContain(token, Text(attached));
            var found = Read<TreeSearchResult>(await client.CallToolAsync("search",
                new Dictionary<string, object?>
                    { ["query"] = "RemoteMcpTarget", ["kind"] = "Logical" }));
            var node = Assert.Single(found.Matches);
            var properties = Read<PropertyInfoDto[]>(await client.CallToolAsync("props",
                new Dictionary<string, object?> { ["nodeId"] = node.Id }));
            var property = Assert.Single(properties,
                item => item.Name == "Text" && item.DeclaringType == typeof(TextBlock).FullName);
            Assert.NotEqual(true,
                (await client.CallToolAsync("set-prop",
                    new Dictionary<string, object?>
                        { ["nodeId"] = node.Id, ["propertyName"] = property.Id, ["value"] = "Changed over WS" }))
                .IsError);
            Assert.Equal("Changed over WS", text.Text);
            var assets = Read<AssetInfo[]>(await client.CallToolAsync("assets", new Dictionary<string, object?>()));
            var asset = Assert.Single(assets,
                item => item.Uri.Contains("AvaloniaDevTool.Cli.Tests/Assets/asset-long-utf8.txt",
                    StringComparison.Ordinal));
            var preview = Read<AssetPreview>(await client.CallToolAsync("open-asset",
                new Dictionary<string, object?> { ["assetUrl"] = asset.Uri }));
            Assert.True(preview.IsTruncated);
            var path = Path.Combine(outputDirectory, "full-resource.txt");
            var exported = Read<AssetExportResult>(await client.CallToolAsync("export-asset",
                new Dictionary<string, object?>
                    { ["uri"] = asset.Uri, ["destinationPath"] = path }));
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.True(bytes.Length > 1024 * 1024);
            Assert.Equal(bytes.LongLength, exported.ByteLength);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), exported.Sha256);
            Assert.True((await client.CallToolAsync("export-asset",
                new Dictionary<string, object?>
                    { ["uri"] = asset.Uri, ["destinationPath"] = path })).IsError);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.NotEqual(true, (await client.CallToolAsync("detach", new Dictionary<string, object?>())).IsError);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (agent.IsConnected || text.Text != "Original remote text")
                await Task.Delay(10, timeout.Token);
        }
        finally
        {
            window.Close();
            Directory.Delete(outputDirectory, true);
        }
    }

    [Fact]
    public async Task Missing_remote_token_and_insecure_urls_fail_without_echoing_credentials()
    {
        await using var client = await CreateClientAsync(null);
        var missing = await client.CallToolAsync("attach-to-remote",
            new Dictionary<string, object?>
            {
                ["endpoint"] = "ws://127.0.0.1:34567/diagnostics",
                ["tokenEnvironmentVariable"] = "AVDT_MISSING_" + Guid.NewGuid().ToString("N")
            },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(missing.IsError);
        Assert.Contains("remote_token_missing", Text(missing));
        var bad = await client.CallToolAsync("attach-to-remote",
            new Dictionary<string, object?>
                { ["endpoint"] = "http://example.invalid/", ["tokenEnvironmentVariable"] = "AVDT_MISSING" },
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(bad.IsError);
        Assert.Contains("invalid_endpoint", Text(bad));
    }

    private static Task<McpClient> CreateClientAsync(IDictionary<string, string?>? environment)
    {
        var assembly = typeof(RemoteMcpTests).Assembly.Location;
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "Remote MCP integration",
            Arguments =
            [
                "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"), "--depsfile",
                Path.ChangeExtension(assembly, ".deps.json"), typeof(Program).Assembly.Location, "mcp", "--profile",
                "extended"
            ],
            EnvironmentVariables = environment, ShutdownTimeout = TimeSpan.FromSeconds(5)
        }));
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text));

    private static T Read<T>(CallToolResult result)
    {
        Assert.False(result.IsError == true, Text(result));
        var node = result.StructuredContent is { } structured
            ? JsonNode.Parse(JsonSerializer.Serialize(structured))
            : JsonNode.Parse(Text(result));
        if (node is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            node = wrapped;
        return node!.Deserialize<T>(ProtocolJson.Options)!;
    }
}
