using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class TreePagingMcpTests
{
    [AvaloniaFact]
    public async Task Stdio_direct_child_pages_expose_large_branches_continuations_changes_and_recursive_truncation()
    {
        var panel = new StackPanel { Name = "McpPagedPanel" };
        var target = new Window { Content = panel };
        target.Show();
        for (var index = 0; index < 2305; index++)
            panel.Children.Add(new Border { Name = "McpPaged" + index });
        try
        {
            await using var agent = Attach(target);
            await using var client = await CreateClientAsync();
            Read<SessionInfo>(await client.CallToolAsync("attach-to-app",
                new Dictionary<string, object?> { ["pipeName"] = agent.PipeName }));
            var tools = await client.ListToolsAsync();
            Assert.Contains(tools, tool => tool.Name == "tree-children");
            Assert.Contains(tools, tool => tool.Name == "template-parent-selector");
            var node = Assert.Single(Read<TreeSearchResult>(await client.CallToolAsync("search",
                new Dictionary<string, object?>
                    { ["query"] = "McpPagedPanel", ["kind"] = "Logical" })).Matches);
            var page = Read<TreeChildrenPage>(await client.CallToolAsync("tree-children",
                new Dictionary<string, object?>
                    { ["nodeId"] = node.Id, ["kind"] = "Logical", ["count"] = 1000 }));
            Assert.Equal(2305, page.TotalCount);
            Assert.Equal(1000, page.Children.Count);
            Assert.True(page.HasMore);
            var all = page.Children.ToList();
            while (page.HasMore)
            {
                page = Read<TreeChildrenPage>(await client.CallToolAsync("tree-children",
                    new Dictionary<string, object?>
                    {
                        ["nodeId"] = node.Id, ["kind"] = "Logical", ["count"] = 1000, ["cursor"] = page.NextCursor
                    }));
                Assert.Equal(all.Count, page.Offset);
                all.AddRange(page.Children);
            }

            Assert.Equal(2305, all.Count);
            Assert.Equal(2305, all.Select(child => child.Id).Distinct().Count());
            var pagedTree = Read<ToolTreeSnapshot>(await client.CallToolAsync("tree",
                new Dictionary<string, object?>
                    { ["nodeId"] = node.Id, ["kind"] = "Logical", ["offset"] = 2300, ["count"] = 3 }));
            Assert.Equal(2300, pagedTree.Page!.Offset);
            Assert.True(pagedTree.Truncated);
            Assert.True(pagedTree.Page.HasMore);
            Assert.Equal(all.Skip(2300).Take(3).Select(child => child.Id),
                pagedTree.Roots.Single().Children.Select(child => child.Node.Id));
            var recursive = Read<ToolTreeSnapshot>(await client.CallToolAsync("tree",
                new Dictionary<string, object?>
                    { ["nodeId"] = node.Id, ["kind"] = "Logical", ["maxNodes"] = 5, ["depth"] = -1 }));
            Assert.True(recursive.Truncated);
            Assert.Equal(5, recursive.Visited);
            Assert.Null(recursive.Page);
            panel.Children.Add(new Border());
            var stale = await client.CallToolAsync("tree-children",
                new Dictionary<string, object?>
                    { ["nodeId"] = node.Id, ["kind"] = "Logical", ["cursor"] = pagedTree.Page.NextCursor });
            Assert.True(stale.IsError);
            Assert.Contains("cursor_invalidated", Text(stale));
            var invalid = await client.CallToolAsync("tree-children",
                new Dictionary<string, object?>
                    { ["nodeId"] = node.Id, ["kind"] = "Logical", ["count"] = 2001 });
            Assert.True(invalid.IsError);
            Assert.Contains("invalid_request", Text(invalid));
            Read<TreeObservationResult>(await client.CallToolAsync("tree-release",
                new Dictionary<string, object?>
                    { ["nodeIds"] = new[] { node.Id }, ["kind"] = "Logical" }));
        }
        finally
        {
            target.Close();
        }
    }

    [AvaloniaFact]
    public async Task Stdio_template_parent_selector_is_independent_from_the_matching_part_path()
    {
        var button = new Button
        {
            Name = "McpTemplateOwner",
            Template = new FuncControlTemplate<Button>((_, _) => new Border { Name = "McpTemplatePart" })
        };
        var target = new Window { Content = button };
        target.Show();
        target.UpdateLayout();
        try
        {
            await using var agent = Attach(target);
            await using var client = await CreateClientAsync();
            Read<SessionInfo>(await client.CallToolAsync("attach-to-app",
                new Dictionary<string, object?> { ["pipeName"] = agent.PipeName }));
            var part = Assert.Single(Read<TreeSearchResult>(await client.CallToolAsync("search",
                new Dictionary<string, object?>
                    { ["query"] = "McpTemplatePart", ["kind"] = "Merged" })).Matches);
            var partSelector = Read<ElementSelectorResult>(await client.CallToolAsync("element-selector",
                new Dictionary<string, object?> { ["nodeId"] = part.Id }));
            var ownerSelector = Read<TemplateParentSelectorResult>(
                await client.CallToolAsync("template-parent-selector",
                    new Dictionary<string, object?> { ["nodeId"] = part.Id }));
            Assert.True(partSelector.Supported);
            Assert.True(partSelector.IsUnique);
            Assert.Equal(1, partSelector.MatchCount);
            Assert.True(ownerSelector.Selector.Supported);
            Assert.True(ownerSelector.Selector.IsUnique);
            Assert.Equal(1, ownerSelector.Selector.MatchCount);
            Assert.Equal("Button#McpTemplateOwner", ownerSelector.Selector.Selector);
            Assert.Contains("/template/", partSelector.Selector);
            Assert.Contains("Border#McpTemplatePart", partSelector.Selector);
            Assert.Equal(part.TemplateOwnerId, ownerSelector.TemplateParentId);
            var noOwner = Read<TemplateParentSelectorResult>(await client.CallToolAsync("template-parent-selector",
                new Dictionary<string, object?>
                    { ["nodeId"] = ownerSelector.TemplateParentId }));
            Assert.False(noOwner.Selector.Supported);
            Assert.Null(noOwner.TemplateParentId);
        }
        finally
        {
            target.Close();
        }
    }

    private static DiagnosticsAgent Attach(Window target) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions
            { PipeName = "tree-pages-mcp-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });

    private static Task<McpClient> CreateClientAsync()
    {
        var assembly = typeof(TreePagingMcpTests).Assembly.Location;
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "Tree paging integration",
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
