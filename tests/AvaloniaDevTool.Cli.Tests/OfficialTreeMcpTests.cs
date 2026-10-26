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

public sealed class OfficialTreeMcpTests
{
    [AvaloniaFact]
    public async Task Official_tree_observes_roots_children_depth_and_full_subtrees_beyond_64_levels()
    {
        var leaf = new Border { Name = "DepthLeaf" };
        Control branch = leaf;
        for (var level = 80; level >= 1; level--)
            branch = new Border { Name = "Depth" + level, Child = branch };
        var panel = new StackPanel { Name = "DepthScope", Children = { branch, new TextBlock { Name = "Sibling" } } };
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            var roots = Read<Snapshot>(await Call(client, "tree", ("depth", 1)));
            var explicitZero = Read<Snapshot>(await Call(client, "tree", ("nodeId", 0), ("depth", 1)));
            Assert.Single(roots.Entries);
            Assert.Equal(roots.Entries.Select(entry => entry.Node.Id),
                explicitZero.Entries.Select(entry => entry.Node.Id));
            Assert.All(roots.Entries,
                entry =>
                {
                    Assert.Null(entry.ParentId);
                    Assert.Equal(0, entry.Depth);
                });
            var scope = Assert.Single(Read<TreeSearchResult>(await Call(client, "search", ("query", "DepthScope")))
                .Matches);
            var direct = Read<Snapshot>(await Call(client,
                "tree",
                ("nodeId", scope.Id),
                ("treeKind", "Logical"),
                ("depth", 1)));
            Assert.Equal(new[] { "Depth1", "Sibling" }, direct.Entries.Select(entry => entry.Node.Name));
            Assert.All(direct.Entries, entry => Assert.Equal(scope.Id, entry.ParentId));
            var defaultDepth = Read<Snapshot>(await Call(client, "tree", ("nodeId", scope.Id)));
            var explicitDepth = Read<Snapshot>(await Call(client, "tree", ("nodeId", scope.Id), ("depth", 10)));
            Assert.Equal(defaultDepth.Entries.Select(entry => entry.Node.Id),
                explicitDepth.Entries.Select(entry => entry.Node.Id));
            Assert.Equal(9, defaultDepth.Entries.Max(entry => entry.Depth));
            Assert.DoesNotContain(defaultDepth.Entries, entry => entry.Node.Name == "DepthLeaf");
            var complete = Read<Snapshot>(await Call(client, "tree", ("nodeId", scope.Id), ("depth", -1)));
            Assert.Contains(complete.Entries, entry => entry.Node.Name == "DepthLeaf" && entry.Depth == 80);
            Assert.Equal(82, complete.Entries.Count);
            Assert.Equal(complete.Entries.Count, complete.Entries.Select(entry => entry.Node.Id).Distinct().Count());
            var leafNode = Assert.Single(complete.Entries, entry => entry.Node.Name == "DepthLeaf").Node;
            Assert.Equal(83, Read<TreeNodeInfo[]>(await Call(client, "ancestors", ("nodeId", leafNode.Id))).Length);
            Assert.Empty(Read<Snapshot>(await Call(client, "tree", ("nodeId", scope.Id), ("depth", 0))).Entries);
            Assert.True((await Call(client, "tree", ("depth", -2))).IsError);
            Assert.True((await Call(client, "tree", ("treeKind", "Unknown"))).IsError);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Official_search_uses_type_name_scope_template_switch_and_unlimited_default()
    {
        var scope = new StackPanel { Name = "SearchScope" };
        for (var index = 0; index < 135; index++)
            scope.Children.Add(new TextBlock { Name = "ManyResult" + index, Text = "PreviewOnlyNeedle" });
        var button = new Button
        {
            Name = "TemplateOwner",
            Template = new FuncControlTemplate<Button>((_, _) => new Border { Name = "HiddenTemplateNeedle" })
        };
        scope.Children.Add(button);
        var window = new Window
            { Content = new StackPanel { Children = { scope, new TextBlock { Name = "OutsideScope" } } } };
        window.Show();
        window.UpdateLayout();
        try
        {
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            var many = Read<TreeSearchResult>(await Call(client, "search", ("query", "ManyResult")));
            Assert.Equal(135, many.Matches.Count);
            Assert.False(many.Truncated);
            Assert.Equal(many.Matches.Select(node => node.Id),
                Read<TreeSearchResult>(await Call(client, "search", ("query", "ManyResult"), ("limit", -1))).Matches
                    .Select(node => node.Id));
            Assert.Equal(7,
                Read<TreeSearchResult>(await Call(client, "search", ("query", "ManyResult"), ("limit", 7))).Matches
                    .Count);
            Assert.Empty(Read<TreeSearchResult>(await Call(client, "search", ("query", "ManyResult"), ("limit", 0)))
                .Matches);
            Assert.True((await Call(client, "search", ("query", "ManyResult"), ("limit", -2))).IsError);
            Assert.Equal(136,
                Read<TreeSearchResult>(await Call(client, "search", ("query", "TextBlock"))).Matches.Count);
            Assert.Empty(Read<TreeSearchResult>(await Call(client, "search", ("query", "PreviewOnlyNeedle"))).Matches);
            var scopeNode = Assert.Single(Read<TreeSearchResult>(await Call(client, "search", ("query", "SearchScope")))
                .Matches);
            Assert.Empty(Read<TreeSearchResult>(await Call(client,
                "search",
                ("query", "OutsideScope"),
                ("scopeNodeId", scopeNode.Id))).Matches);
            Assert.Single(
                Read<TreeSearchResult>(await Call(client, "search", ("query", "OutsideScope"), ("scopeNodeId", 0)))
                    .Matches);
            Assert.Empty(Read<TreeSearchResult>(await Call(client,
                "search",
                ("query", "HiddenTemplateNeedle"),
                ("searchTemplates", false))).Matches);
            var templatePart = Assert.Single(Read<TreeSearchResult>(await Call(client,
                "search",
                ("query", "HiddenTemplateNeedle"),
                ("searchTemplates", true))).Matches);
            var owner = Assert.Single(Read<TreeSearchResult>(await Call(client, "search", ("query", "TemplateOwner")))
                .Matches);
            Assert.Equal(templatePart.Id,
                Assert.Single(Read<TreeSearchResult>(await Call(client,
                    "search",
                    ("query", "HiddenTemplateNeedle"),
                    ("scopeNodeId", owner.Id),
                    ("searchTemplates", true))).Matches).Id);
            var logical = Read<Snapshot>(await Call(client,
                "tree",
                ("nodeId", owner.Id),
                ("treeKind", "Logical"),
                ("depth", -1)));
            var visual = Read<Snapshot>(await Call(client,
                "tree",
                ("nodeId", owner.Id),
                ("treeKind", "Visual"),
                ("depth", -1)));
            var merged = Read<Snapshot>(await Call(client,
                "tree",
                ("nodeId", owner.Id),
                ("treeKind", "Merged"),
                ("depth", -1)));
            Assert.Empty(logical.Entries);
            Assert.Contains(visual.Entries, entry => entry.Node.Id == templatePart.Id);
            Assert.Contains(merged.Entries, entry => entry.Node.Role == TreeNodeRole.TemplateGroup);
            Assert.Contains(merged.Entries, entry => entry.Node.Id == templatePart.Id);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Official_ancestors_and_tree_handles_preserve_identity_and_reject_detached_nodes()
    {
        var target = new TextBlock { Name = "AncestorTarget", Text = "target" };
        var parent = new Border { Name = "AncestorParent", Child = target };
        var window = new Window { Name = "AncestorRoot", Content = parent };
        window.Show();
        try
        {
            await using var agent = Attach(window);
            await using var client = await CreateClientAsync(agent.PipeName);
            var node = Assert.Single(Read<TreeSearchResult>(await Call(client, "search", ("query", "AncestorTarget")))
                .Matches);
            var chain = Read<TreeNodeInfo[]>(await Call(client, "ancestors", ("nodeId", node.Id)));
            Assert.Equal(new[] { "AncestorRoot", "AncestorParent", "AncestorTarget" }, chain.Select(item => item.Name));
            var rootChain = Read<TreeNodeInfo[]>(await Call(client, "ancestors", ("nodeId", chain[0].Id)));
            Assert.Equal(chain[0].Id, Assert.Single(rootChain).Id);
            var children = Read<Snapshot>(await Call(client, "tree", ("nodeId", chain[1].Id), ("depth", 1)));
            Assert.Equal(node.Id, Assert.Single(children.Entries).Node.Id);
            var props = await Call(client, "props", ("nodeId", node.Id));
            Assert.NotEqual(true, props.IsError);
            var screenshot = await Call(client, "screenshot", ("nodeId", node.Id));
            Assert.False(screenshot.IsError == true, Text(screenshot));
            var image = Assert.Single(screenshot.Content.OfType<ImageContentBlock>());
            Assert.Equal("image/png", image.MimeType);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, image.DecodedData.ToArray()[..8]);
            var action = await Call(client, "action", ("nodeId", node.Id), ("action", "Disable"));
            Assert.NotEqual(true, action.IsError);
            Assert.False(target.IsEnabled);
            parent.Child = null;
            foreach (var result in new[]
                     {
                         await Call(client, "tree", ("nodeId", node.Id)),
                         await Call(client, "ancestors", ("nodeId", node.Id)),
                         await Call(client, "search", ("query", "AncestorTarget"), ("scopeNodeId", node.Id))
                     })
            {
                Assert.True(result.IsError);
                Assert.Contains("object_detached", Text(result));
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static DiagnosticsAgent Attach(Window window) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions
            { PipeName = "official-tree-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });

    private static Task<McpClient> CreateClientAsync(string pipe)
    {
        var assembly = typeof(OfficialTreeMcpTests).Assembly.Location;
        return McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "Official tree integration",
            Arguments =
            [
                "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"), "--depsfile",
                Path.ChangeExtension(assembly, ".deps.json"), typeof(Program).Assembly.Location, "mcp", "--profile",
                "official", "--pipe", pipe
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

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static T Read<T>(CallToolResult result)
    {
        Assert.False(result.IsError == true, Text(result));
        var root = result.StructuredContent is { } content
            ? JsonNode.Parse(JsonSerializer.Serialize(content))
            : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }

    private sealed record Snapshot(IReadOnlyList<Entry> Entries);

    private sealed record Entry(TreeNodeInfo Node, long? ParentId, int Depth);
}
