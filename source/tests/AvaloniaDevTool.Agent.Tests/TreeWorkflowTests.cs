using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using SkiaSharp;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class TreeWorkflowTests
{
    [AvaloniaFact]
    public async Task Expanded_branches_use_real_collection_events_and_release_stops_notifications()
    {
        var panel = new StackPanel { Name = "ObservedPanel", Children = { new TextBlock { Name = "First" } } };
        await using var target = await Target.CreateAsync(panel);
        var node = (await target.Client.Tree.SearchTreeAsync(new("ObservedPanel", TreeKind.Visual))).Matches.Single();
        await target.Client.Tree.GetChildrenAsync(node.Id, TreeKind.Visual);
        var invalidations = 0;
        target.Client.Tree.TreeInvalidated += change =>
        {
            if (change.ParentId == node.Id)
                Interlocked.Increment(ref invalidations);
        };
        panel.Children.Add(new TextBlock { Name = "Second" });
        await WaitUntilAsync(() => Volatile.Read(ref invalidations) > 0);
        await target.Client.Tree.ReleaseTreeAsync([node.Id], TreeKind.Visual);
        var releasedCount = Volatile.Read(ref invalidations);
        panel.Children.Add(new TextBlock { Name = "Third" });
        await Task.Delay(400);
        Assert.Equal(releasedCount, Volatile.Read(ref invalidations));
        Assert.Equal(3, (await target.Client.Tree.GetChildrenAsync(node.Id, TreeKind.Visual)).Count);
        panel.Children.RemoveAt(0);
        await WaitUntilAsync(() => Volatile.Read(ref invalidations) > releasedCount);
    }

    [AvaloniaFact]
    public async Task Diagnostic_overlay_changes_do_not_invalidate_an_observed_layer_but_application_children_do()
    {
        await using var target =
            await Target.CreateAsync(new Border { Name = "OverlayTarget", Width = 100, Height = 50 });
        var layer = Assert.IsType<AdornerLayer>(AdornerLayer.GetAdornerLayer(target.Window));
        target.Window.UpdateLayout();
        var element = (await target.Client.Tree.SearchTreeAsync(new("OverlayTarget", TreeKind.Visual))).Matches
            .Single();
        var layerNode =
            (await target.Client.Tree.SearchTreeAsync(new("AdornerLayer", TreeKind.Visual))).Matches.Single();
        await target.Client.Tree.GetChildrenAsync(layerNode.Id, TreeKind.Visual);
        var invalidations = 0;
        target.Client.Tree.TreeInvalidated += change =>
        {
            if (change.ParentId == layerNode.Id)
                Interlocked.Increment(ref invalidations);
        };
        await target.Client.Selection.HighlightAsync(element.Id);
        await target.Client.Selection.HighlightAsync(null);
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref invalidations));
        var businessChild = new Border { Name = "ApplicationAdorner" };
        layer.Children.Add(businessChild);
        await WaitUntilAsync(() => Volatile.Read(ref invalidations) == 1);
        layer.Children.Remove(businessChild);
        await WaitUntilAsync(() => Volatile.Read(ref invalidations) == 2);
    }

    [AvaloniaFact]
    public void Selector_validation_keeps_the_last_unvisited_node_in_the_truncation_budget()
    {
        var target = new TextBlock { Name = "BudgetDuplicate" };
        var duplicate = new TextBlock { Name = "BudgetDuplicate" };
        var truncated = ElementSelectorBuilder.Build(target, [target, duplicate], maximumValidationNodes: 1);
        Assert.True(truncated.Supported, truncated.Detail);
        Assert.True(truncated.ValidationTruncated);
        Assert.False(truncated.IsUnique);
        var complete = ElementSelectorBuilder.Build(target, [target, duplicate], maximumValidationNodes: 2);
        Assert.False(complete.ValidationTruncated);
        Assert.Equal(2, complete.MatchCount);
        Assert.False(complete.IsUnique);
    }

    [AvaloniaFact]
    public async Task Removing_a_root_releases_its_tree_observations_without_releasing_property_observers()
    {
        var panel = new StackPanel { Name = "RemovedRootPanel" };
        var window = new Window { Content = panel };
        window.Show();
        var includeRoot = true;
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                {
                    PipeName = "tree-root-lifecycle-" + Guid.NewGuid().ToString("N"),
                    TopLevelProvider = () => includeRoot ? [window] : []
                });
            await using var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            var panelNode = (await client.Tree.SearchTreeAsync(new("RemovedRootPanel", TreeKind.Logical))).Matches
                .Single();
            await client.Tree.GetChildrenAsync(panelNode.Id, TreeKind.Logical);
            await client.Properties.GetPropertiesAsync(panelNode.Id);
            Assert.Equal(1, (await client.Tree.ReleaseTreeAsync([], TreeKind.Logical)).ObservedBranches);
            includeRoot = false;
            Assert.Empty(await client.Tree.GetRootsAsync(TreeKind.Logical));
            Assert.Equal(0, (await client.Tree.ReleaseTreeAsync([], TreeKind.Logical)).ObservedBranches);
            var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Properties.PropertiesChanged += change =>
            {
                if (change.ObjectId == panelNode.Id)
                    notified.TrySetResult();
            };
            panel.Opacity = 0.5;
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Selector_uses_Avalonia_parser_and_reports_ambiguity_templates_and_unsupported_identifiers()
    {
        var first = new TextBlock { Name = "StablePart", Classes = { "primary" } };
        var duplicate = new StackPanel { Children = { new TextBlock(), new TextBlock() } };
        var templated = new Button
            { Name = "Owner", Template = new FuncControlTemplate<Button>((_, _) => new Border { Name = "Chrome" }) };
        var badName = new TextBlock { Name = "unsafe.name" };
        var panel = new StackPanel { Children = { first, duplicate, templated, badName } };
        await using var target = await Target.CreateAsync(panel);
        var all = (await target.Client.Tree.SearchTreeAsync(new("", TreeKind.Visual, MaxResults: 500))).Matches;
        var selected = all.Single(node => node.Name == "StablePart");
        var selector = await target.Client.Tree.GetElementSelectorAsync(selected.Id);
        Assert.True(selector.Supported, selector.Detail);
        Assert.Equal("TextBlock#StablePart.primary", selector.Selector);
        Assert.True(selector.IsUnique);
        var metadata = await target.Client.Tree.GetElementMetadataAsync(selected.Id);
        Assert.Equal(target.Client.Session!.SessionId, metadata.SessionId);
        Assert.Equal(selected.Id, metadata.ObjectId);
        Assert.Equal(["primary"], metadata.Classes);
        var anonymous = all.First(node => node.TypeName == typeof(TextBlock).FullName && node.Name is null);
        var ambiguous = await target.Client.Tree.GetElementSelectorAsync(anonymous.Id);
        Assert.True(ambiguous.Supported, ambiguous.Detail);
        Assert.False(ambiguous.IsUnique);
        Assert.True(ambiguous.MatchCount >= 2);
        var chrome = all.Single(node => node.Name == "Chrome");
        var templateSelector = await target.Client.Tree.GetElementSelectorAsync(chrome.Id);
        Assert.True(templateSelector.Supported, templateSelector.Detail);
        Assert.Contains("/template/", templateSelector.Selector);
        Assert.True(templateSelector.IsUnique);
        Assert.False(
            (await target.Client.Tree.GetElementSelectorAsync(all.Single(node => node.Name == "unsafe.name").Id))
            .Supported);
        first.Classes.Add("unsafe.class");
        Assert.False((await target.Client.Tree.GetElementSelectorAsync(selected.Id)).Supported);
    }

    [AvaloniaFact]
    public async Task BringIntoView_changes_scroll_offset_and_moves_target_into_the_viewport()
    {
        var item = new Border { Name = "BelowViewport", Width = 50, Height = 50, Background = Brushes.Red };
        var scroll = new ScrollViewer
            { Content = new StackPanel { Children = { new Border { Height = 1000 }, item } } };
        await using var target = await Target.CreateAsync(scroll);
        target.Window.UpdateLayout();
        Assert.Equal(0, scroll.Offset.Y);
        using var before = Capture(target.Window);
        Assert.Equal(0, RedPixels(before));
        var node = (await target.Client.Tree.SearchTreeAsync(new("BelowViewport", TreeKind.Visual))).Matches.Single();
        Assert.True((await target.Client.Input.PerformElementActionAsync(node.Id, ElementAction.BringIntoView))
            .Succeeded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        target.Window.UpdateLayout();
        Assert.True(scroll.Offset.Y > 700, $"Scroll offset was {scroll.Offset.Y}.");
        var origin = item.TranslatePoint(default, scroll);
        Assert.NotNull(origin);
        Assert.InRange(origin.Value.Y, -1, scroll.Bounds.Height);
        using var after = Capture(target.Window);
        Assert.True(RedPixels(after) >= 1000, "The scrolled target must be visible in the rendered viewport pixels.");
    }

    [AvaloniaFact]
    public void Active_tree_observation_does_not_keep_a_detached_control_alive()
    {
        using var session = new AgentSession(Application.Current!,
            new DiagnosticsAgentOptions { TopLevelProvider = () => [] });
        var weak = ObserveDetached(session);
        for (var index = 0; index < 3; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(weak.IsAlive);
        GC.KeepAlive(session);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ObserveDetached(AgentSession session)
    {
        var panel = new StackPanel();
        var registry = (WeakObjectRegistry)typeof(AgentSession).GetField("_objects",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;
        var id = registry.GetId(panel);
        session.Handle(TreeWorkflowMethods.Observe,
            System.Text.Json.JsonSerializer.SerializeToElement(new TreeObserveRequest(id, TreeKind.Visual),
                ProtocolJson.Options));
        return new WeakReference(panel);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }

    private static int RedPixels(SKBitmap bitmap) =>
        bitmap.Pixels.Count(color => color.Red > 230 && color.Green < 20 && color.Blue < 20);

    private static SKBitmap Capture(Window window)
    {
        window.InvalidateVisual();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var stream = new MemoryStream();
        frame.Save(stream, new PngBitmapEncoderOptions());
        return SKBitmap.Decode(stream.ToArray());
    }

    private sealed class Target(Window window, DiagnosticsAgent agent, DiagnosticsClient client) : IAsyncDisposable
    {
        public Window Window { get; } = window;
        public DiagnosticsClient Client { get; } = client;

        public static async Task<Target> CreateAsync(Control content)
        {
            var window = new Window { Content = content, Width = 400, Height = 300 };
            window.Show();
            window.UpdateLayout();
            var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                    { PipeName = "tree-workflow-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            return new(window, agent, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await agent.DisposeAsync();
            Window.Close();
        }
    }
}
