using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class MergedTreeInspectorTests
{
    [AvaloniaFact]
    public async Task Merged_groups_and_slots_explain_their_roles_and_navigate_to_real_controls()
    {
        var content = new TextBlock { Name = "UserContent", Text = "User content" };
        var button = new Button { Name = "TemplateOwner", Content = content, Template = Template("Chrome") };
        var target = new Window { Content = button };
        target.Show();
        target.UpdateLayout();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 2 };
            await vm.InitializeAsync();
            Assert.Equal(3, vm.TreeKinds.Count);
            Assert.Contains("Merged", vm.TreeKinds[2]);
            var owner = await OwnerAsync(vm);
            owner.IsExpanded = true;
            await WaitUntilAsync(() => owner.Children.Any(node => node.Node.Role == TreeNodeRole.TemplateGroup));
            var user = owner.Children.Single(node => node.Node.Name == "UserContent");
            var group = owner.Children.Single(node => node.Node.Role == TreeNodeRole.TemplateGroup);
            Assert.True(group.IsSynthetic);
            Assert.DoesNotContain("MergedTree", group.TypeName);
            Assert.Contains("模板", group.TypeName);
            Assert.Equal("边界", group.RoleLabel);
            vm.SelectedNode = group;
            await WaitUntilAsync(() => vm.Status == vm.SyntheticSelectionNotice);
            Assert.True(vm.IsSyntheticSelection);
            Assert.Empty(vm.FilteredProperties);
            Assert.Contains("合成边界", vm.SyntheticSelectionNotice);
            Assert.DoesNotContain("AvaloniaDevTool.Agent", vm.SelectedTitle);
            Assert.DoesNotContain("AvaloniaDevTool.Agent", vm.SelectedSubtitle);
            Assert.False(vm.FocusElementCommand.CanExecute(null));
            Assert.False(vm.BringIntoViewCommand.CanExecute(null));
            Assert.False(vm.EnableElementCommand.CanExecute(null));
            Assert.False(vm.DisableElementCommand.CanExecute(null));
            Assert.False(vm.CaptureElementCommand.CanExecute(null));
            vm.InspectionTabIndex = 9;
            await vm.RefreshRuntimeAsync();
            Assert.False(vm.RefreshRuntimeCommand.CanExecute(null));
            Assert.Null(vm.RuntimeObjectId);
            Assert.Empty(vm.RuntimeRows);
            Assert.True(vm.NavigateTemplateOwnerCommand.CanExecute(null));
            Assert.False(vm.NavigateSlotContentCommand.CanExecute(null));
            vm.NavigateTemplateOwnerCommand.Execute(null);
            await WaitUntilAsync(() => vm.SelectedNode?.Id == owner.Id && vm.FilteredProperties.Count > 0 && !vm.RuntimeIsLoading && vm.RuntimeRows.Count > 0);
            Assert.False(vm.IsSyntheticSelection);
            Assert.True(vm.RefreshRuntimeCommand.CanExecute(null));
            group.IsExpanded = true;
            await WaitUntilAsync(() => group.Children.Any(node => node.Node.Role == TreeNodeRole.TemplateRoot));
            var chrome = group.Children.Single(node => node.Node.Name == "Chrome");
            Assert.Equal("模板根", chrome.RoleLabel);
            Assert.False(chrome.IsSynthetic);
            chrome.IsExpanded = true;
            await WaitUntilAsync(() => chrome.Children.Any(node => node.Node.Name == "PART_ContentPresenter"));
            var presenter = chrome.Children.Single(node => node.Node.Name == "PART_ContentPresenter");
            Assert.Equal(TreeNodeRole.TemplatePart, presenter.Node.Role);
            presenter.IsExpanded = true;
            await WaitUntilAsync(() => presenter.Children.Any(node => node.Node.Role == TreeNodeRole.ContentSlot));
            var slot = presenter.Children.Single(node => node.Node.Role == TreeNodeRole.ContentSlot);
            Assert.True(slot.IsSynthetic);
            Assert.Equal(user.Id, slot.Node.SlotContentId);
            Assert.DoesNotContain("MergedTree", slot.TypeName);
            vm.SelectedNode = slot;
            await WaitUntilAsync(() => vm.Status == vm.SyntheticSelectionNotice);
            Assert.Contains("实际内容", vm.SyntheticSelectionNotice);
            Assert.Empty(vm.FilteredProperties);
            Assert.Empty(vm.RuntimeRows);
            Assert.True(vm.NavigateSlotContentCommand.CanExecute(null));
            vm.NavigateSlotContentCommand.Execute(null);
            await WaitUntilAsync(() => vm.SelectedNode?.Id == user.Id && vm.FilteredProperties.Any(row => row.Name == "Text"));
            Assert.False(vm.IsSyntheticSelection);
            Assert.True(vm.CaptureElementCommand.CanExecute(null));
            var root = Assert.Single(vm.Roots);
            var publishedOwner = new TaskCompletionSource<Task<TreeNodeViewModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnLogicalRootPublished(object? sender, NotifyCollectionChangedEventArgs change)
            {
                if (change.Action == NotifyCollectionChangedAction.Add && vm.Roots.Count == 1 &&
                    !ReferenceEquals(root, vm.Roots[0]) && !publishedOwner.Task.IsCompleted)
                {
                    // Start reading at root publication, before lazy child loading can complete.
                    publishedOwner.TrySetResult(OwnerAsync(vm));
                }
            }
            TreeNodeViewModel logicalOwner;
            vm.Roots.CollectionChanged += OnLogicalRootPublished;
            try
            {
                vm.SelectedTreeIndex = 1;
                logicalOwner = await (await publishedOwner.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally { vm.Roots.CollectionChanged -= OnLogicalRootPublished; }
            Assert.NotSame(root, Assert.Single(vm.Roots));
            Assert.Equal(owner.Id, logicalOwner.Id);
            logicalOwner.IsExpanded = true;
            await WaitUntilAsync(() => logicalOwner.Children.Any(node => node.Node.Name == "UserContent"));
            Assert.Equal(user.Id, logicalOwner.Children.Single(node => node.Node.Name == "UserContent").Id);
            Assert.DoesNotContain(logicalOwner.Children, node => node.IsSynthetic);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Retemplating_clears_expired_group_selection_and_preserves_a_newer_real_selection()
    {
        var button = new Button
        {
            Name = "TemplateOwner", Content = new TextBlock { Name = "UserContent" }, Template = Template("OldChrome")
        };
        var target = new Window { Content = button };
        target.Show();
        target.UpdateLayout();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 2 };
            await vm.InitializeAsync();
            var owner = await OwnerAsync(vm);
            owner.IsExpanded = true;
            await WaitUntilAsync(() => owner.Children.Any(node => node.Node.Role == TreeNodeRole.TemplateGroup));
            var oldGroup = owner.Children.Single(node => node.Node.Role == TreeNodeRole.TemplateGroup);
            vm.SelectedNode = oldGroup;
            await WaitUntilAsync(() => vm.Status == vm.SyntheticSelectionNotice);
            var oldRoot = Assert.Single(vm.Roots);
            button.Template = Template("SecondChrome");
            button.ApplyTemplate();
            target.UpdateLayout();
            await WaitUntilAsync(() => vm.Roots.Count == 1 && !ReferenceEquals(oldRoot, vm.Roots[0]) && vm.SelectedNode is null && vm.Status.Contains("失效", StringComparison.Ordinal));
            Assert.Empty(vm.FilteredProperties);
            Assert.Empty(vm.RuntimeRows);
            var refreshedOwner = await OwnerAsync(vm);
            Assert.Equal(owner.Id, refreshedOwner.Id);
            await WaitUntilAsync(() => refreshedOwner.Children.Any(node => node.Node.Role == TreeNodeRole.TemplateGroup));
            var newGroup = refreshedOwner.Children.Single(node => node.Node.Role == TreeNodeRole.TemplateGroup);
            Assert.NotEqual(oldGroup.Id, newGroup.Id);
            vm.SelectedNode = newGroup;
            await WaitUntilAsync(() => vm.Status == vm.SyntheticSelectionNotice);
            oldRoot = Assert.Single(vm.Roots);
            button.Template = Template("ThirdChrome");
            button.ApplyTemplate();
            target.UpdateLayout();
            vm.SelectedNode = refreshedOwner;
            await WaitUntilAsync(() => vm.Roots.Count == 1 && !ReferenceEquals(oldRoot, vm.Roots[0]) &&
                vm.Roots[0].Children.Any(node => !node.IsPlaceholder && node.Node.Name == "TemplateOwner" && ReferenceEquals(node, vm.SelectedNode)) &&
                vm.SelectedNode?.Id == owner.Id && vm.FilteredProperties.Count > 0);
            Assert.False(vm.IsSyntheticSelection);
            Assert.Equal(owner.Id, vm.SelectedNode!.Id);
            Assert.DoesNotContain("操作失败", vm.Status);
        }
        finally { target.Close(); }
    }

    private static async Task<TreeNodeViewModel> OwnerAsync(MainWindowViewModel vm)
    {
        await WaitUntilAsync(() => vm.Roots.Count == 1 &&
            vm.Roots[0].Children.Any(node => !node.IsPlaceholder && node.Node.Name == "TemplateOwner"));
        return Assert.Single(vm.Roots).Children.Single(node => node.Node.Name == "TemplateOwner");
    }

    private static FuncControlTemplate<Button> Template(string name) => new((_, _) =>
    {
        var presenter = new ContentPresenter { Name = "PART_ContentPresenter" };
        presenter.Bind(ContentPresenter.ContentProperty, new TemplateBinding(ContentControl.ContentProperty));
        return new Border { Name = name, Child = presenter };
    });

    private static DiagnosticsAgent CreateAgent(Window target) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions
        {
            PipeName = "merged-inspector-tests-" + Guid.NewGuid().ToString("N"),
            ApplicationName = "Merged inspector tests", TopLevelProvider = () => [target]
        });

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
