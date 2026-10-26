using System.IO.Pipes;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class MergedTreeTests
{
    [AvaloniaFact]
    public async Task Template_groups_preserve_user_content_once_and_slots_reference_its_logical_path()
    {
        var content = new TextBlock { Name = "UserContent", Text = "User text" };
        var button = Button(content, "FirstChrome");
        await using var target = await Target.CreateAsync(button);
        var all = await target.SearchAsync("");
        Assert.False(all.Truncated);
        var user = Assert.Single(all.Matches, node => node.Name == "UserContent");
        var owner = Assert.Single(all.Matches, node => node.Name == "TemplateOwner");
        var group = Assert.Single(await target.ChildrenAsync(owner.Id),
            node => node.Role == TreeNodeRole.TemplateGroup);
        Assert.Equal(owner.Id, group.TemplateOwnerId);
        Assert.Contains(await target.ChildrenAsync(owner.Id), node => node.Id == user.Id);
        var chrome = Assert.Single(all.Matches, node => node.Name == "FirstChrome");
        Assert.Equal(TreeNodeRole.TemplateRoot, chrome.Role);
        Assert.Equal(owner.Id, chrome.TemplateOwnerId);
        var slot = Assert.Single(all.Matches,
            node => node.Role == TreeNodeRole.ContentSlot && node.SlotContentId == user.Id);
        Assert.Equal(user.Id, slot.SlotContentId);
        Assert.Empty(await target.ChildrenAsync(slot.Id));
        Assert.Equal(["MergedRoot", "TemplateOwner", "UserContent"],
            (await target.PathAsync(user.Id)).Select(node => node.Name));
        var chromePath = await target.PathAsync(chrome.Id);
        Assert.Equal(group.Id, chromePath[^2].Id);
        foreach (var node in all.Matches)
            Assert.Equal(node.Id, (await target.PathAsync(node.Id))[^1].Id);
        var visual = await target.SearchAsync("", TreeKind.Visual);
        var logical = await target.SearchAsync("", TreeKind.Logical);
        Assert.Contains(visual.Matches, node => node.Name == "FirstChrome");
        Assert.DoesNotContain(logical.Matches, node => node.Name == "FirstChrome");
        Assert.All(visual.Matches.Concat(logical.Matches), node => Assert.Equal(TreeNodeRole.Element, node.Role));
    }

    [AvaloniaFact]
    public async Task Retemplating_invalidates_old_groups_and_old_template_objects_and_notifies_expanded_owner()
    {
        var button = Button(new TextBlock { Name = "StableUser" }, "OldChrome");
        await using var target = await Target.CreateAsync(button);
        var all = await target.SearchAsync("");
        var owner = all.Matches.Single(node => node.Name == "TemplateOwner");
        var user = all.Matches.Single(node => node.Name == "StableUser");
        var oldChrome = all.Matches.Single(node => node.Name == "OldChrome");
        var oldRoot = button.GetVisualChildren().First();
        var oldGroup = (await target.ChildrenAsync(owner.Id)).Single(node => node.Role == TreeNodeRole.TemplateGroup);
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.Peer.NotificationReceived += (method, payload) =>
        {
            if (method == Methods.TreeInvalidated &&
                ProtocolJson.Read<TreeInvalidatedNotification>(payload).ParentId == owner.Id)
                notified.TrySetResult();
        };
        button.Template = Template("NewChrome");
        button.ApplyTemplate();
        target.Window.UpdateLayout();
        await notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("object_expired",
            (await Assert.ThrowsAsync<RpcException>(() => target.ChildrenAsync(oldGroup.Id))).Code);
        Assert.Equal("object_detached",
            (await Assert.ThrowsAsync<RpcException>(() => target.ChildrenAsync(oldChrome.Id))).Code);
        var refreshed = await target.SearchAsync("");
        Assert.DoesNotContain(refreshed.Matches, node => node.Name == "OldChrome");
        Assert.Contains(refreshed.Matches, node => node.Name == "NewChrome");
        Assert.Equal(user.Id, refreshed.Matches.Single(node => node.Name == "StableUser").Id);
        Assert.NotEqual(oldGroup.Id,
            (await target.ChildrenAsync(owner.Id)).Single(node => node.Role == TreeNodeRole.TemplateGroup).Id);
        GC.KeepAlive(oldRoot);
    }

    [AvaloniaFact]
    public async Task Empty_templates_standalone_presenters_and_search_limits_are_explicit()
    {
        var noTemplate = new TemplatedControl { Name = "EmptyTemplate", Template = null };
        var content = new TextBlock { Name = "PlainContent" };
        var presenter = new ContentPresenter { Content = content };
        await using var target = await Target.CreateAsync(new StackPanel { Children = { noTemplate, presenter } });
        var all = await target.SearchAsync("");
        var empty = all.Matches.Single(node => node.Name == "EmptyTemplate");
        Assert.Empty(await target.ChildrenAsync(empty.Id));
        Assert.Single(all.Matches, node => node.Name == "PlainContent");
        Assert.DoesNotContain(all.Matches,
            node => node.Role == TreeNodeRole.ContentSlot &&
                    node.SlotContentId == all.Matches.Single(item => item.Name == "PlainContent").Id);
        var bounded = await target.Peer.InvokeAsync<TreeSearchResult>(ExplorationMethods.Search,
            new TreeSearchRequest("", TreeKind.Merged, MaxVisited: 2));
        Assert.Equal(2, bounded.Visited);
        Assert.True(bounded.Truncated);
        Assert.Equal("invalid_request",
            (await Assert.ThrowsAsync<RpcException>(() => target.Peer.InvokeAsync<TreeNodeInfo[]>(
                Methods.TreeRoots,
                new TreeRootsRequest((TreeKind)123)))).Code);
    }

    [AvaloniaFact]
    public void Retained_synthetic_group_does_not_keep_owner_or_template_root_alive()
    {
        var tree = new MergedTree();
        var (group, owner, root) = DetachedGroup(tree);
        for (var index = 0; index < 3; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(owner.IsAlive);
        Assert.False(root.IsAlive);
        Assert.Equal("object_expired", Assert.Throws<RpcException>(() => tree.Children(group)).Code);
        GC.KeepAlive(group);
        GC.KeepAlive(tree);
    }

    [AvaloniaFact]
    public async Task Replacing_content_expires_old_slot_and_preserves_current_content_without_duplicate_nodes()
    {
        var button = Button(new TextBlock { Name = "OldUserContent" }, "Chrome");
        await using var target = await Target.CreateAsync(button);
        var before = await target.SearchAsync("");
        var oldUser = before.Matches.Single(node => node.Name == "OldUserContent");
        var slot = before.Matches.Single(node =>
            node.Role == TreeNodeRole.ContentSlot && node.SlotContentId == oldUser.Id);
        button.Content = new TextBlock { Name = "NewUserContent" };
        target.Window.UpdateLayout();
        Assert.Equal("object_expired", (await Assert.ThrowsAsync<RpcException>(() => target.PathAsync(slot.Id))).Code);
        Assert.Equal("object_detached",
            (await Assert.ThrowsAsync<RpcException>(() => target.PathAsync(oldUser.Id))).Code);
        var after = await target.SearchAsync("");
        Assert.DoesNotContain(after.Matches, node => node.Name == "OldUserContent");
        var user = Assert.Single(after.Matches, node => node.Name == "NewUserContent");
        Assert.Single(after.Matches, node => node.Role == TreeNodeRole.ContentSlot && node.SlotContentId == user.Id);
        Assert.Equal(["MergedRoot", "TemplateOwner", "NewUserContent"],
            (await target.PathAsync(user.Id)).Select(node => node.Name));
    }

    [AvaloniaFact]
    public async Task Popup_content_is_reachable_once_under_its_current_presentation_root()
    {
        var anchor = new Button { Content = "Open popup" };
        var popup = new Popup
            { Child = new TextBlock { Name = "MergedPopupContent", Text = "Popup" }, PlacementTarget = anchor };
        await using var target = await Target.CreateAsync(new StackPanel { Children = { anchor, popup } });
        popup.Open();
        target.Window.UpdateLayout();
        try
        {
            var roots = await target.Peer.InvokeAsync<TreeNodeInfo[]>(Methods.TreeRoots,
                new TreeRootsRequest(TreeKind.Merged));
            var result = await target.SearchAsync("");
            var content = Assert.Single(result.Matches, node => node.Name == "MergedPopupContent");
            var path = await target.PathAsync(content.Id);
            Assert.Contains(roots, root => root.Id == path[0].Id);
            Assert.Equal(content.Id, path[^1].Id);
            foreach (var node in result.Matches)
                Assert.Equal(node.Id, (await target.PathAsync(node.Id))[^1].Id);
        }
        finally
        {
            popup.Close();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (object Group, WeakReference Owner, WeakReference Root) DetachedGroup(MergedTree tree)
    {
        var owner = Button(new TextBlock(), "CollectibleChrome");
        owner.ApplyTemplate();
        var group = tree.Children(owner).OfType<MergedTree.TemplateGroup>().Single();
        return (group, new WeakReference(owner), new WeakReference(owner.GetVisualChildren().First()));
    }

    private static Button Button(Control content, string chrome) => new()
    {
        Name = "TemplateOwner", Content = content, Template = Template(chrome)
    };

    private static FuncControlTemplate<Button> Template(string chrome) => new((_, _) =>
    {
        var presenter = new ContentPresenter { Name = "PART_ContentPresenter" };
        presenter.Bind(ContentPresenter.ContentProperty, new TemplateBinding(ContentControl.ContentProperty));
        return new Border { Name = chrome, Child = presenter };
    });

    private sealed class Target(Window window, DiagnosticsAgent agent, RpcPeer peer) : IAsyncDisposable
    {
        public Window Window { get; } = window;
        public RpcPeer Peer { get; } = peer;

        public static async Task<Target> CreateAsync(Control content)
        {
            var window = new Window { Name = "MergedRoot", Content = content, Width = 500, Height = 400 };
            window.Show();
            window.UpdateLayout();
            var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                {
                    PipeName = "avdt-merged-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window]
                });
            var pipe = new NamedPipeClientStream(".", agent.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            var peer = new RpcPeer(pipe);
            peer.Start();
            await peer.InvokeAsync<SessionInfo>(Methods.SessionInfo, null);
            return new Target(window, agent, peer);
        }

        public Task<TreeSearchResult> SearchAsync(string query, TreeKind kind = TreeKind.Merged) =>
            Peer.InvokeAsync<TreeSearchResult>(ExplorationMethods.Search, new TreeSearchRequest(query, kind));

        public Task<TreeNodeInfo[]> ChildrenAsync(long id) =>
            Peer.InvokeAsync<TreeNodeInfo[]>(Methods.TreeChildren, new TreeChildrenRequest(id, TreeKind.Merged));

        public Task<TreeNodeInfo[]> PathAsync(long id) =>
            Peer.InvokeAsync<TreeNodeInfo[]>(ExplorationMethods.Ancestors,
                new TreeAncestorsRequest(id, TreeKind.Merged));

        public async ValueTask DisposeAsync()
        {
            await Peer.DisposeAsync();
            await agent.DisposeAsync();
            Window.Close();
        }
    }
}
