using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using System.Reflection;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class TreePagingTests
{
    [AvaloniaFact]
    public void A_unique_match_on_another_style_key_never_validates_the_selected_element()
    {
        var selected = new MaskedTextBox { Name = "StyleKeyCollision" };
        var wrong = new MaskedStyleKeyControl { Name = "StyleKeyCollision" };
        var selector = new Style(value => value.OfType<MaskedTextBox>().Name("StyleKeyCollision")).Selector!;
        var match = typeof(Selector).GetMethod("Match",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        var isMatch = match.ReturnType.GetProperty("IsMatch")!;
        Assert.False((bool)isMatch.GetValue(match.Invoke(selector, [selected, null, false]))!);
        Assert.True((bool)isMatch.GetValue(match.Invoke(selector, [wrong, null, false]))!);
        var result = ElementSelectorBuilder.Build(selected, [selected, wrong]);
        Assert.False(result.Supported);
        Assert.False(result.IsUnique);
        Assert.Null(result.Selector);
        Assert.Contains("selected element", result.Detail);
    }

    [AvaloniaFact]
    public async Task Direct_children_beyond_two_thousand_are_reachable_with_bounded_pages_and_stable_ids()
    {
        var panel = new StackPanel { Name = "PagedPanel" };
        await using var target = await Target.CreateAsync(panel);
        for (var index = 0; index < 2305; index++)
            panel.Children.Add(new Border { Name = "PagedChild" + index });
        var id = (await target.Client.Tree.SearchTreeAsync(new("PagedPanel", TreeKind.Logical))).Matches.Single().Id;
        var first = await target.Client.Tree.GetChildrenPageAsync(id, TreeKind.Logical, count: 37);
        Assert.Equal(2305, first.TotalCount);
        Assert.Equal(37, first.Children.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursor);
        var all = first.Children.ToList();
        var page = first;
        while (page.HasMore)
        {
            page = await target.Client.Tree.GetChildrenPageAsync(id,
                TreeKind.Logical,
                count: 113,
                cursor: page.NextCursor);
            Assert.Equal(all.Count, page.Offset);
            Assert.InRange(page.Children.Count, 1, 113);
            all.AddRange(page.Children);
        }

        Assert.Equal(2305, all.Count);
        Assert.Equal(2305, all.Select(node => node.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 2305).Select(index => "PagedChild" + index), all.Select(node => node.Name));
        Assert.Null(page.NextCursor);
        var visual = await target.Client.Tree.GetChildrenPageAsync(id, TreeKind.Visual, offset: 2299, count: 10);
        var merged = await target.Client.Tree.GetChildrenPageAsync(id, TreeKind.Merged, offset: 2299, count: 10);
        Assert.Equal(all.Skip(2299).Select(node => node.Id), visual.Children.Select(node => node.Id));
        Assert.Equal(visual.Children.Select(node => node.Id), merged.Children.Select(node => node.Id));
        Assert.All(merged.Children, node => Assert.Equal(TreeNodeRole.Element, node.Role));
    }

    [AvaloniaFact]
    public async Task Cursors_reject_branch_changes_even_when_original_order_is_restored_and_release_discards_state()
    {
        var panel = new StackPanel { Name = "CursorPanel", Children = { new Border(), new Border(), new Border() } };
        await using var target = await Target.CreateAsync(panel);
        var id = (await target.Client.Tree.SearchTreeAsync(new("CursorPanel", TreeKind.Logical))).Matches.Single().Id;
        var page = await target.Client.Tree.GetChildrenPageAsync(id, TreeKind.Logical, count: 1);
        var child = panel.Children[0];
        panel.Children.RemoveAt(0);
        panel.Children.Insert(0, child);
        var stale = await Assert.ThrowsAsync<RpcException>(() =>
            target.Client.Tree.GetChildrenPageAsync(id, TreeKind.Logical, cursor: page.NextCursor));
        Assert.Equal("cursor_invalidated", stale.Code);
        page = await target.Client.Tree.GetChildrenPageAsync(id, TreeKind.Logical, count: 1);
        Assert.Equal("cursor_invalidated",
            (await Assert.ThrowsAsync<RpcException>(() =>
                target.Client.Tree.GetChildrenPageAsync(id, TreeKind.Visual, cursor: page.NextCursor))).Code);
        await target.Client.Tree.ReleaseTreeAsync([id], TreeKind.Logical);
        Assert.Equal(0, (await target.Client.Tree.ReleaseTreeAsync([], TreeKind.Logical)).ObservedBranches);
        Assert.Equal("cursor_invalidated",
            (await Assert.ThrowsAsync<RpcException>(() =>
                target.Client.Tree.GetChildrenPageAsync(id, TreeKind.Logical, cursor: page.NextCursor))).Code);
        Assert.Equal("invalid_request",
            (await Assert.ThrowsAsync<RpcException>(() =>
                target.Client.Tree.GetChildrenPageAsync(id, TreeKind.Logical, count: 2001))).Code);
        Assert.Equal("invalid_request",
            (await Assert.ThrowsAsync<RpcException>(() =>
                target.Client.Tree.GetChildrenPageAsync(id, TreeKind.Logical, offset: 4))).Code);
        Assert.Empty((await target.Client.Tree.GetChildrenPageAsync(id, TreeKind.Logical, offset: 3)).Children);
    }

    [AvaloniaFact]
    public async Task Oversized_branches_fail_explicitly_instead_of_silently_returning_a_prefix()
    {
        var panel = new StackPanel { Name = "OversizedPagePanel" };
        await using var target = await Target.CreateAsync(panel);
        var id = (await target.Client.Tree.SearchTreeAsync(new("OversizedPagePanel", TreeKind.Logical))).Matches
            .Single().Id;
        for (var index = 0; index < 20001; index++)
            panel.Children.Add(new Border());
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            target.Client.Tree.GetChildrenPageAsync(id, TreeKind.Logical, count: 1));
        Assert.Equal("tree_limit", error.Code);
        Assert.Equal(0, (await target.Client.Tree.ReleaseTreeAsync([], TreeKind.Logical)).ObservedBranches);
    }

    [AvaloniaFact]
    public async Task Template_parent_and_part_have_distinct_real_matching_selectors_and_merged_page_identity()
    {
        var button = new Button
        {
            Name = "SelectorOwner",
            Template = new FuncControlTemplate<Button>((_, _) => new Border { Name = "SelectorPart" })
        };
        await using var target = await Target.CreateAsync(button);
        var owner = (await target.Client.Tree.SearchTreeAsync(new("SelectorOwner", TreeKind.Merged))).Matches.Single();
        var part = (await target.Client.Tree.SearchTreeAsync(new("SelectorPart", TreeKind.Merged))).Matches.Single();
        var partSelector = await target.Client.Tree.GetElementSelectorAsync(part.Id);
        var parentSelector = await target.Client.Tree.GetTemplateParentSelectorAsync(part.Id);
        Assert.Equal(owner.Id, parentSelector.TemplateParentId);
        Assert.True(partSelector.Supported, partSelector.Detail);
        Assert.True(partSelector.IsUnique);
        Assert.Equal(1, partSelector.MatchCount);
        Assert.True(parentSelector.Selector.Supported, parentSelector.Selector.Detail);
        Assert.True(parentSelector.Selector.IsUnique);
        Assert.Equal(1, parentSelector.Selector.MatchCount);
        Assert.Contains("/template/", partSelector.Selector);
        Assert.Equal("Button#SelectorOwner", parentSelector.Selector.Selector);
        Assert.NotEqual(partSelector.Selector, parentSelector.Selector.Selector);
        var group = Assert.Single((await target.Client.Tree.GetChildrenPageAsync(owner.Id, TreeKind.Merged, count: 1))
            .Children);
        Assert.Equal(TreeNodeRole.TemplateGroup, group.Role);
        Assert.Equal(owner.Id, group.TemplateOwnerId);
        Assert.Equal(group.Id,
            Assert.Single((await target.Client.Tree.GetChildrenPageAsync(owner.Id, TreeKind.Merged)).Children).Id);
        Assert.Equal(owner.Id, (await target.Client.Tree.GetTemplateParentSelectorAsync(group.Id)).TemplateParentId);
        var templateRoot =
            Assert.Single((await target.Client.Tree.GetChildrenPageAsync(group.Id, TreeKind.Merged)).Children);
        Assert.Equal(TreeNodeRole.TemplateRoot, templateRoot.Role);
        Assert.Equal(part.Id, templateRoot.Id);
        Assert.False((await target.Client.Tree.GetTemplateParentSelectorAsync(owner.Id)).Selector.Supported);
    }

    [AvaloniaFact]
    public async Task Custom_template_owner_requires_namespace_mapping_for_both_selector_entries()
    {
        var button = new CustomTemplateOwner
        {
            Template = new FuncControlTemplate<CustomTemplateOwner>((_, _) => new Border { Name = "CustomOwnerPart" })
        };
        await using var target = await Target.CreateAsync(button);
        var part = (await target.Client.Tree.SearchTreeAsync(new("CustomOwnerPart", TreeKind.Visual))).Matches.Single();
        var parent = await target.Client.Tree.GetTemplateParentSelectorAsync(part.Id);
        Assert.NotNull(parent.TemplateParentId);
        Assert.False(parent.Selector.Supported);
        Assert.Null(parent.Selector.Selector);
        Assert.Contains("namespace mapping", parent.Selector.Detail);
        var partSelector = await target.Client.Tree.GetElementSelectorAsync(part.Id);
        Assert.False(partSelector.Supported);
        Assert.Contains("namespace mapping", partSelector.Detail);
    }

    private sealed class CustomTemplateOwner : Button;

    private sealed class MaskedStyleKeyControl : Control
    {
        protected override Type StyleKeyOverride => typeof(MaskedTextBox);
    }

    private sealed class Target(Window window, DiagnosticsAgent agent, DiagnosticsClient client) : IAsyncDisposable
    {
        public DiagnosticsClient Client { get; } = client;

        public static async Task<Target> CreateAsync(Control content)
        {
            var window = new Window { Content = content, Width = 300, Height = 200 };
            window.Show();
            window.UpdateLayout();
            var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                    { PipeName = "tree-page-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            return new(window, agent, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await agent.DisposeAsync();
            window.Close();
        }
    }
}
