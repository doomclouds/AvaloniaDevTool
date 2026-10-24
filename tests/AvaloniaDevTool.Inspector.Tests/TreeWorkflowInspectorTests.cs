using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class TreeWorkflowInspectorTests
{
    [AvaloniaFact]
    public async Task Recursive_expansion_has_real_node_depth_and_template_budgets_and_can_cancel()
    {
        var panel = new StackPanel { Name = "BudgetPanel" };
        for (var index = 0; index < 20; index++) panel.Children.Add(new Button
        {
            Name = "Button" + index, Content = new TextBlock { Name = "Content" + index },
            Template = new FuncControlTemplate<Button>((_, _) => new Border { Name = "Chrome" })
        });
        var target = new Window { Content = panel }; target.Show(); target.UpdateLayout();
        try
        {
            await using var agent = Attach(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 2 };
            await vm.InitializeAsync();
            var node = Assert.Single(vm.Roots).Children.Single(item => item.Node.Name == "BudgetPanel");
            var result = await vm.ExpandSubtreeAsync(node, new(MaxNodes: 3));
            Assert.True(result.Truncated); Assert.Equal(3, result.Visited);
            Assert.Equal(2, node.Children.Count);
            Assert.Contains("截断", vm.TreeExpansionNotice);
            await vm.CollapseSubtreeAsync(node);
            Assert.False(node.IsExpanded); Assert.True(Assert.Single(node.Children).IsPlaceholder);
            result = await vm.ExpandSubtreeAsync(node, new(MaxNodes: 25, IncludeTemplates: false));
            Assert.True(result.Truncated);
            Assert.InRange(Walk(node).Count(item => !item.IsPlaceholder), 1, 25);
            await vm.CollapseSubtreeAsync(node);
            result = await vm.ExpandSubtreeAsync(node, new(MaxNodes: 500, IncludeTemplates: false));
            Assert.False(result.Truncated);
            var groups = Walk(node).Where(item => item.Node.Role == TreeNodeRole.TemplateGroup).ToArray();
            Assert.Equal(20, groups.Length); Assert.All(groups, item => Assert.False(item.IsExpanded));
            Assert.DoesNotContain(Walk(node), item => item.Node.Name == "Chrome");
            await vm.CollapseSubtreeAsync(node);
            result = await vm.ExpandSubtreeAsync(node, new(MaxNodes: 500, IncludeTemplates: true));
            Assert.False(result.Truncated); Assert.Equal(20, Walk(node).Count(item => item.Node.Name == "Chrome"));
            await vm.CollapseSubtreeAsync(node);
            result = await vm.ExpandSubtreeAsync(node, new(MaxDepth: 0));
            Assert.True(result.Truncated); Assert.False(node.IsExpanded);
            using var stop = new CancellationTokenSource();
            node.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(TreeNodeViewModel.IsExpanded) && node.IsExpanded) stop.Cancel(); };
            result = await vm.ExpandSubtreeAsync(node, new(BatchSize: 1), stop.Token);
            Assert.True(result.Cancelled); Assert.False(vm.IsExpandingTree); Assert.Contains("取消", vm.TreeExpansionNotice);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Collapsed_subtree_releases_backend_observations_and_reexpands_with_fresh_children()
    {
        var panel = new StackPanel { Name = "LazyPanel", Children = { new TextBlock { Name = "Original" } } };
        var target = new Window { Content = panel }; target.Show();
        try
        {
            await using var agent = Attach(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            var root = Assert.Single(vm.Roots);
            var node = root.Children.Single(item => item.Node.Name == "LazyPanel");
            await vm.ExpandSubtreeAsync(node);
            var firstId = node.Children.Single(item => item.Node.Name == "Original").Id;
            vm.SelectedNode = node.Children.Single(item => item.Id == firstId);
            await WaitUntilAsync(() => vm.FilteredProperties.Count > 0);
            await vm.CollapseSubtreeAsync(node);
            panel.Children.Add(new TextBlock { Name = "WhileFolded" });
            await Task.Delay(600);
            Assert.Same(root, Assert.Single(vm.Roots));
            Assert.False(node.IsExpanded); Assert.True(Assert.Single(node.Children).IsPlaceholder);
            await vm.ExpandSubtreeAsync(node);
            Assert.Equal(firstId, node.Children.Single(item => item.Node.Name == "Original").Id);
            Assert.Contains(node.Children, item => item.Node.Name == "WhileFolded");
            Assert.Equal(firstId, vm.SelectedNode!.Id);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Window_commands_copy_actual_selector_and_session_scoped_metadata_to_the_platform_clipboard()
    {
        var target = new Window { Content = new TextBlock { Name = "ClipboardElement", Classes = { "primary" } } };
        target.Show();
        var host = new MainWindow();
        try
        {
            await using var agent = Attach(target);
            var vm = host.ViewModel;
            vm.SelectedTreeIndex = 1; vm.PipeName = agent.PipeName;
            host.Show();
            vm.ConnectCommand.Execute(null);
            await WaitUntilAsync(() => vm.Roots.Count == 1 && vm.Roots[0].Children.Any(node => node.Node.Name == "ClipboardElement"));
            vm.SelectedNode = vm.Roots[0].Children.Single(node => node.Node.Name == "ClipboardElement");
            await WaitUntilAsync(() => vm.FilteredProperties.Count > 0);
            var copy = Find<Button>(host, "Inspector.CopySelector");
            Assert.True(copy.IsEnabled); copy.Command!.Execute(null);
            await WaitUntilAsync(() => vm.Status.Contains("已复制", StringComparison.Ordinal));
            Assert.Equal("TextBlock#ClipboardElement.primary", await host.Clipboard!.TryGetTextAsync());
            Find<Button>(host, "Inspector.CopyElementMetadata").Command!.Execute(null);
            await WaitUntilAsync(() => vm.Status.Contains("元数据 JSON", StringComparison.Ordinal));
            using var json = JsonDocument.Parse((await host.Clipboard!.TryGetTextAsync())!);
            Assert.Equal("native", json.RootElement.GetProperty("backendId").GetString());
            Assert.Equal(vm.SelectedNode.Id, json.RootElement.GetProperty("objectId").GetInt64());
            Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("sessionId").GetString()));
            Assert.NotNull(Find<NumericUpDown>(host, "Inspector.ExpansionMaxNodes"));
            Assert.NotNull(Find<Button>(host, "Inspector.CancelTreeExpansion"));
        }
        finally { host.Close(); await host.ViewModel.DisposeAsync(); target.Close(); }
    }

    [AvaloniaFact]
    public async Task Large_accumulated_tree_release_is_batched_with_the_real_agent_request_limit()
    {
        var target = new Window { Content = new StackPanel { Name = "BatchReleasePanel", Children = { new TextBlock() } } };
        target.Show();
        try
        {
            await using var agent = Attach(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            await vm.ExpandSubtreeAsync(vm.Roots.Single());
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var client = (DiagnosticsClient)typeof(MainWindowViewModel).GetField("_client", flags)!.GetValue(vm)!;
            var ids = Enumerable.Repeat(-1L, 40000).Concat(Walk(vm.Roots.Single()).Where(node => !node.IsPlaceholder).Select(node => node.Id)).ToArray();
            Assert.True((await client.ReleaseTreeAsync([], TreeKind.Logical)).ObservedBranches > 0);
            var release = typeof(MainWindowViewModel).GetMethod("ReleaseTreeBranchesAsync", flags)!;
            await (Task)release.Invoke(vm, [ids, TreeKind.Logical, CancellationToken.None])!;
            Assert.Equal(0, (await client.ReleaseTreeAsync([], TreeKind.Logical)).ObservedBranches);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => (Task)release.Invoke(vm, [ids, TreeKind.Logical, cancelled.Token])!);
        }
        finally { target.Close(); }
    }

    private static IEnumerable<TreeNodeViewModel> Walk(TreeNodeViewModel node) => new[] { node }.Concat(node.Children.SelectMany(Walk));
    private static T Find<T>(Visual root, string id) where T : Control => root.GetVisualDescendants().OfType<T>()
        .Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static DiagnosticsAgent Attach(Window target) => DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
    { PipeName = "tree-inspector-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}

