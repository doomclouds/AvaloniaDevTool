using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class TreePagingInspectorTests
{
    [AvaloniaFact]
    public async Task Actual_load_more_button_appends_pages_and_search_locates_a_later_page_without_losing_selection_on_fold()
    {
        var panel = new StackPanel { Name = "InspectorPagedPanel" };
        for (var index = 0; index < 235; index++) panel.Children.Add(new Border { Name = "InspectorPaged" + index });
        var target = new Window { Content = panel }; target.Show();
        var host = new MainWindow();
        try
        {
            await using var agent = Attach(target);
            var vm = host.ViewModel; vm.SelectedTreeIndex = 1; vm.PipeName = agent.PipeName; host.Show();
            vm.ConnectCommand.Execute(null);
            await WaitUntilAsync(() => vm.Roots.Count == 1 && vm.Roots[0].Children.Any(node => node.Node.Name == "InspectorPagedPanel"), vm);
            var branch = vm.Roots[0].Children.Single(node => node.Node.Name == "InspectorPagedPanel");
            vm.SelectedNode = branch;
            Click(host, Find<Button>(host, "Inspector.ExpandNode"));
            await WaitUntilAsync(() => branch.Children.Count == 100 && !branch.IsLoading, vm);
            Assert.True(branch.HasMoreChildren); Assert.Equal(235, branch.TotalChildrenCount);
            var expander = Find<Expander>(host, "Inspector.TreePagingTools");
            Assert.False(expander.IsExpanded); Click(host, expander);
            await WaitUntilAsync(() => expander.IsExpanded, vm);
            host.UpdateLayout(); Assert.True(Find<TreeView>(host, "Inspector.Tree").Bounds.Height > 50);
            var more = Find<Button>(host, "Inspector.LoadMoreChildren");
            Assert.True(more.IsEnabled); Click(host, more);
            await WaitUntilAsync(() => branch.Children.Count == 200 && !branch.IsLoading, vm);
            Assert.Equal(200, branch.Children.Select(child => child.Id).Distinct().Count());
            Assert.Same(branch, vm.SelectedNode);
            vm.TreeSearchQuery = "InspectorPaged234"; Click(host, Find<Button>(host, "Inspector.SearchTree"));
            await WaitUntilAsync(() => vm.TreeSearchResults.Count == 1, vm);
            vm.SelectedSearchResult = Assert.Single(vm.TreeSearchResults);
            await WaitUntilAsync(() => vm.SelectedNode?.Node.Name == "InspectorPaged234", vm);
            Assert.Equal(235, branch.Children.Count); Assert.False(branch.HasMoreChildren);
            var selectedId = vm.SelectedNode!.Id;
            await vm.CollapseSubtreeAsync(branch);
            Assert.Equal(selectedId, vm.SelectedNode!.Id);
            branch.IsExpanded = true;
            await WaitUntilAsync(() => branch.Children.Count == 235 && !branch.IsLoading, vm);
            Assert.Same(branch.Children.Single(child => child.Id == selectedId), vm.SelectedNode);
            await vm.CollapseSubtreeAsync(branch);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            var result = await vm.ExpandSubtreeAsync(branch, new(MaxNodes: 5), stop.Token);
            Assert.True(result.Cancelled); Assert.Equal(selectedId, vm.SelectedNode!.Id);
        }
        finally { host.Close(); await host.ViewModel.DisposeAsync(); target.Close(); }
    }

    [AvaloniaFact]
    public async Task Template_parent_copy_and_navigation_are_independent_reachable_pointer_actions_with_real_clipboard()
    {
        var owner = new Button { Name = "ClipboardTemplateOwner", Template = new FuncControlTemplate<Button>((_, _) => new Border { Name = "ClipboardTemplatePart" }) };
        var target = new Window { Content = owner }; target.Show(); target.UpdateLayout();
        var host = new MainWindow();
        try
        {
            await using var agent = Attach(target);
            var vm = host.ViewModel; vm.SelectedTreeIndex = 2; vm.PipeName = agent.PipeName; host.Show();
            vm.ConnectCommand.Execute(null);
            await WaitUntilAsync(() => vm.Roots.Count == 1, vm);
            vm.TreeSearchQuery = "ClipboardTemplatePart"; Click(host, Find<Button>(host, "Inspector.SearchTree"));
            await WaitUntilAsync(() => vm.TreeSearchResults.Count == 1, vm);
            vm.SelectedSearchResult = vm.TreeSearchResults.Single();
            await WaitUntilAsync(() => vm.SelectedNode?.Node.Name == "ClipboardTemplatePart", vm);
            var partId = vm.SelectedNode!.Id;
            Click(host, Find<Button>(host, "Inspector.CopySelector"));
            await WaitUntilAsync(() => vm.Status.Contains("已复制 Avalonia Selector", StringComparison.Ordinal), vm);
            var partText = await host.Clipboard!.TryGetTextAsync();
            Assert.Contains("/template/", partText); Assert.Contains("Border#ClipboardTemplatePart", partText);
            var expander = Find<Expander>(host, "Inspector.TreePagingTools"); Click(host, expander);
            await WaitUntilAsync(() => expander.IsExpanded, vm);
            Click(host, Find<Button>(host, "Inspector.CopyTemplateParentSelector"));
            await WaitUntilAsync(() => vm.Status.Contains("已复制模板父 Selector", StringComparison.Ordinal), vm);
            Assert.Equal("Button#ClipboardTemplateOwner", await host.Clipboard!.TryGetTextAsync());
            Assert.Equal(partId, vm.SelectedNode!.Id);
            Click(host, Find<Button>(host, "Inspector.CopySelectorFromTemplateParent"));
            await WaitUntilAsync(() => vm.Status.Contains("已复制从模板父到所选部件", StringComparison.Ordinal), vm);
            Assert.Equal(partText, await host.Clipboard!.TryGetTextAsync());
            Assert.Contains("Button#ClipboardTemplateOwner /template/ Border#ClipboardTemplatePart", await host.Clipboard!.TryGetTextAsync());
            Assert.Equal(partId, vm.SelectedNode!.Id);
            var selectedPart = vm.SelectedNode;
            var pending = vm.GetSelectedSelectorFromTemplateParentAsync();
            vm.SelectedNode = vm.Roots.Single().Children.Single(node => node.Node.Name == "ClipboardTemplateOwner");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(partText, await host.Clipboard!.TryGetTextAsync());
            vm.SelectedNode = selectedPart;
            Click(host, Find<Button>(host, "Inspector.NavigateSelectorTemplateParent"));
            await WaitUntilAsync(() => vm.SelectedNode?.Node.Name == "ClipboardTemplateOwner", vm);
            Assert.NotEqual(partId, vm.SelectedNode!.Id);
        }
        finally { host.Close(); await host.ViewModel.DisposeAsync(); target.Close(); }
    }

    private static DiagnosticsAgent Attach(Window target) => DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
    { PipeName = "tree-paging-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
    private static T Find<T>(Visual root, string id) where T : Control => root.GetVisualDescendants().OfType<T>()
        .Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static void Click(Window host, Control control)
    {
        control.BringIntoView(); host.UpdateLayout(); using var frame = host.CaptureRenderedFrame();
        Assert.True(control.IsEffectivelyVisible); Assert.True(control.IsEnabled);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host);
        Assert.NotNull(point); Assert.True(new Rect(host.ClientSize).Contains(point.Value));
        host.MouseMove(point.Value); host.MouseDown(point.Value, MouseButton.Left); host.MouseUp(point.Value, MouseButton.Left);
    }
    private static async Task WaitUntilAsync(Func<bool> predicate, MainWindowViewModel vm)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!predicate() && !timeout.IsCancellationRequested) await Task.Delay(15);
        Assert.True(predicate(), vm.Status + " | " + vm.TreeExpansionNotice);
    }
}
