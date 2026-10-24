using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class BindingNavigationTests
{
    [AvaloniaFact]
    public async Task Axaml_source_opens_public_members_nested_cycle_and_returns_to_original_binding_without_reselecting()
    {
        var source = new NavigationModel();
        var control = new TextBlock { Name = "BindingTarget", DataContext = source };
        control.Bind(TextBlock.TextProperty, new Binding(nameof(NavigationModel.Name)));
        var target = new Window { Content = control };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await SelectBindingAsync(vm);
            var selected = vm.SelectedNode;
            var binding = TextBinding(vm);
            var styles = new StylesView { DataContext = vm };
            var inspector = new Window { Width = 1000, Height = 900, Content = styles };
            inspector.Show();
            try
            {
                var sourceButton = styles.GetVisualDescendants().OfType<Button>().First(button =>
                    AutomationProperties.GetAutomationId(button) == "Inspector.BindingNavigate" &&
                    button.DataContext is BindingDetailRowViewModel { Name: "来源对象" });
                Assert.True(sourceButton.Command!.CanExecute(null));
                var originalSourceCommand = sourceButton.Command;
                originalSourceCommand.Execute(null);
                await WaitUntilAsync(() => vm.InspectionTabIndex == 9 && !vm.RuntimeIsLoading && vm.RuntimeRows.Any(row => row.Name == "Child"));
                Assert.Same(selected, vm.SelectedNode);
                Assert.Equal(binding.Details!.Source.ObjectId, vm.RuntimeObjectId);
                Assert.Equal(2, vm.RuntimeBreadcrumbs.Count);
                Assert.Contains("Text", vm.BindingNavigationContext);
                Assert.Contains(vm.RuntimeRows, row => row.Name == "Name" && row.Preview == "Original");
                var self = Assert.Single(vm.RuntimeRows, row => row.Name == "Self");
                Assert.True(self.IsSelfReference);
                var child = Assert.Single(vm.RuntimeRows, row => row.Name == "Child");
                child.OpenCommand.Execute(null);
                await WaitUntilAsync(() => vm.RuntimeObjectId == child.ValueObjectId && !vm.RuntimeIsLoading);
                var parent = Assert.Single(vm.RuntimeRows, row => row.Name == "Parent");
                Assert.True(parent.IsCycleReference);
                parent.OpenCommand.Execute(null);
                await WaitUntilAsync(() => vm.RuntimeObjectId == binding.Details.Source.ObjectId && !vm.RuntimeIsLoading);
                Assert.Equal(2, vm.RuntimeBreadcrumbs.Count);
                var runtimeView = new RuntimeView { DataContext = vm };
                inspector.Content = runtimeView;
                inspector.UpdateLayout();
                var back = Assert.Single(runtimeView.GetVisualDescendants().OfType<Button>(), button =>
                    AutomationProperties.GetAutomationId(button) == "Inspector.RuntimeReturnBinding");
                Assert.True(back.Command!.CanExecute(null));
                back.Command.Execute(null);
                await WaitUntilAsync(() => vm.InspectionTabIndex == 1 && vm.Status.Contains("返回绑定", StringComparison.Ordinal));
                Assert.Same(selected, vm.SelectedNode);
                Assert.Contains("返回绑定", vm.Status);
                Assert.False(originalSourceCommand.CanExecute(null));
                var dataContext = Field(vm, "DataContext");
                Assert.True(dataContext.NavigateCommand!.CanExecute(null));
                dataContext.NavigateCommand.Execute(null);
                await WaitUntilAsync(() => vm.InspectionTabIndex == 9 && !vm.RuntimeIsLoading && vm.RuntimeRows.Any(row => row.Name == "Name"));
                Assert.Contains("DataContext", vm.BindingNavigationContext);
                vm.RuntimeUseSelectionCommand.Execute(null);
                await WaitUntilAsync(() => vm.RuntimeObjectId == selected!.Id && !vm.RuntimeIsLoading);
                Assert.Same(selected, vm.SelectedNode);
            }
            finally { inspector.Close(); }
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Expired_source_keeps_origin_and_error_without_old_rows_and_refresh_can_recover_only_by_new_observation()
    {
        var control = new TextBlock { Name = "BindingTarget" };
        var weak = SetTransientDataContext(control);
        control.Bind(TextBlock.TextProperty, new Binding(nameof(NavigationModel.Name)));
        var target = new Window { Content = control };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await SelectBindingAsync(vm);
            var source = Field(vm, "来源对象");
            var oldId = TextBinding(vm).Details!.Source.ObjectId;
            control.DataContext = null;
            Collect();
            Assert.False(weak.IsAlive);
            source.NavigateCommand!.Execute(null);
            await WaitUntilAsync(() => vm.InspectionTabIndex == 9 && vm.RuntimeHasError && !vm.RuntimeIsLoading);
            Assert.Equal(oldId, vm.RuntimeObjectId);
            Assert.Contains("object_expired", vm.RuntimeError);
            Assert.Empty(vm.RuntimeRows);
            Assert.True(vm.ReturnToBindingCommand.CanExecute(null));
            await vm.RefreshRuntimeAsync();
            Assert.Contains("object_expired", vm.RuntimeError);
            Assert.Empty(vm.RuntimeRows);
            vm.ReturnToBindingCommand.Execute(null);
            await WaitUntilAsync(() => vm.InspectionTabIndex == 1 && TextBinding(vm).Details!.Source.State == BindingDetailState.Null);
            Assert.Null(Field(vm, "来源对象").NavigateCommand);
            Assert.False(source.NavigateCommand.CanExecute(null));
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Real_source_reentrancy_selection_page_disconnect_and_reconnect_invalidate_old_navigation()
    {
        var source = new NavigationModel();
        var first = new TextBlock { Name = "BindingTarget", DataContext = source };
        first.Bind(TextBlock.TextProperty, new Binding(nameof(NavigationModel.Name)));
        var second = new TextBlock { Name = "OtherTarget", Text = "Other" };
        var target = new Window { Content = new StackPanel { Name = "NavigationPanel", Children = { first, second } } };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            var panel = Assert.Single(Assert.Single(vm.Roots).Children, node => node.Node.Name == "NavigationPanel");
            panel.IsExpanded = true;
            await WaitUntilAsync(() => panel.Children.Count == 2);
            var firstNode = panel.Children.Single(node => node.Node.Name == "BindingTarget");
            var secondNode = panel.Children.Single(node => node.Node.Name == "OtherTarget");
            vm.SelectedNode = firstNode;
            vm.InspectionTabIndex = 1;
            await WaitUntilAsync(() => vm.BindingDiagnostics.Count > 0);
            var oldSource = Field(vm, "来源对象");
            source.OnRead = () => vm.SelectedNode = secondNode;
            oldSource.NavigateCommand!.Execute(null);
            await WaitUntilAsync(() => vm.SelectedNode == secondNode && !vm.RuntimeIsLoading && vm.RuntimeObjectId == secondNode.Id && vm.RuntimeRows.Count > 0);
            Assert.False(vm.HasBindingNavigationOrigin);
            Assert.DoesNotContain(vm.RuntimeRows, row => row.Name == nameof(NavigationModel.AReentrantGetter));
            Assert.False(oldSource.NavigateCommand.CanExecute(null));
            vm.InspectionTabIndex = 1;
            vm.SelectedNode = firstNode;
            await WaitUntilAsync(() => vm.BindingDiagnostics.Count > 0);
            source.OnRead = () => vm.InspectionTabIndex = 0;
            Field(vm, "来源对象").NavigateCommand!.Execute(null);
            await WaitUntilAsync(() => vm.InspectionTabIndex == 0 && !vm.RuntimeIsLoading);
            Assert.Empty(vm.RuntimeRows);
            vm.InspectionTabIndex = 1;
            await vm.RefreshActiveInspectionAsync();
            var disconnectSource = Field(vm, "来源对象");
            source.OnRead = () => vm.DisconnectCommand.Execute(null);
            disconnectSource.NavigateCommand!.Execute(null);
            await WaitUntilAsync(() => !vm.IsConnected && vm.RuntimeObjectId is null);
            Assert.False(vm.HasBindingNavigationOrigin);
            Assert.False(vm.ReturnToBindingCommand.CanExecute(null));
            Assert.False(disconnectSource.NavigateCommand.CanExecute(null));
            vm.ConnectCommand.Execute(null);
            await WaitUntilAsync(() => vm.IsConnected && vm.Roots.Count > 0);
            Assert.False(disconnectSource.NavigateCommand.CanExecute(null));
            Assert.False(vm.HasBindingNavigationOrigin);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Dynamic_resource_uses_real_key_and_preserves_multiple_scope_provenances_and_nested_owner()
    {
        const string key = "NavigationAccent";
        var merged = new ResourceDictionary { [key] = "Merged" };
        var control = new TextBlock { Name = "BindingTarget", Resources = new ResourceDictionary
        { [key] = "Local", MergedDictionaries = { merged } } };
        control.Bind(TextBlock.TextProperty, new DynamicResourceExtension(key));
        Application.Current!.Resources[key] = "Application";
        var target = new Window { Content = control };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await SelectBindingAsync(vm);
            var binding = TextBinding(vm);
            Assert.Equal("dynamic-resource", binding.Details!.Kind);
            Assert.Equal(key, binding.Details.ResourceKey!.Value);
            Assert.Equal(typeof(string).FullName, binding.Details.ResourceKey.TypeName);
            var selected = vm.SelectedNode;
            Field(vm, "ResourceKey").NavigateCommand!.Execute(null);
            await WaitUntilAsync(() => vm.InspectionTabIndex == 2 && vm.BindingResourceMatches.Count == 3 && vm.BindingResourceSummary.Contains("候选来源", StringComparison.Ordinal));
            Assert.Same(selected, vm.SelectedNode);
            Assert.Equal(key, vm.ResourceKeyFilter);
            Assert.True(vm.IncludeNestedResources);
            Assert.Equal(3, vm.BindingResourceMatches.Select(row => row.Entry.ProviderPath).Distinct().Count());
            var nested = Assert.Single(vm.BindingResourceMatches, row => row.Entry.ProviderPath.Contains("Merged", StringComparison.Ordinal));
            nested.ShowOwnerCommand.Execute(null);
            await WaitUntilAsync(() => vm.SelectedResourceDictionary?.Id == nested.Entry.OwnerDictionaryId && vm.ResourceEntries.Count == 1);
            Assert.Equal("Merged", Assert.Single(vm.ResourceEntries).Draft);
            var resourceView = new ResourcesView { DataContext = vm };
            var inspector = new Window { Width = 1000, Height = 800, Content = resourceView };
            inspector.Show();
            try
            {
                Assert.Single(resourceView.GetVisualDescendants().OfType<Control>(), item =>
                    AutomationProperties.GetAutomationId(item) == "Inspector.BindingResourceMatches");
                Assert.Single(resourceView.GetVisualDescendants().OfType<Button>(), item =>
                    AutomationProperties.GetAutomationId(item) == "Inspector.ResourceReturnBinding").Command!.Execute(null);
                await WaitUntilAsync(() => vm.InspectionTabIndex == 1 && vm.BindingDiagnostics.Count > 0);
                Assert.Same(selected, vm.SelectedNode);
            }
            finally { inspector.Close(); }
        }
        finally { target.Close(); Application.Current!.Resources.Remove(key); }
    }

    [AvaloniaFact]
    public async Task Non_string_resource_key_opens_actual_key_object_without_parsing_display_text()
    {
        var key = new NavigationKey();
        var control = new TextBlock { Name = "BindingTarget", Resources = new ResourceDictionary { [key] = "Actual value" } };
        control.Bind(TextBlock.TextProperty, new DynamicResourceExtension(key));
        var target = new Window { Content = control };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await SelectBindingAsync(vm);
            var resourceKey = TextBinding(vm).Details!.ResourceKey!;
            Assert.NotNull(resourceKey.ObjectId);
            Assert.Equal(typeof(NavigationKey).FullName, resourceKey.Value);
            Field(vm, "ResourceKey").NavigateCommand!.Execute(null);
            await WaitUntilAsync(() => vm.InspectionTabIndex == 9 && !vm.RuntimeIsLoading && vm.RuntimeRows.Count > 0);
            Assert.Equal(resourceKey.ObjectId, vm.RuntimeObjectId);
            Assert.Contains(vm.RuntimeRows, row => row.Name == "Identity" && row.Preview == "Actual key");
            Assert.False(vm.HasBindingResourceQuery);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Foreign_handles_and_missing_key_facts_stay_disabled_and_filters_do_not_mutate_raw_evidence()
    {
        await using var backend = new ForeignBackend();
        await using var vm = new MainWindowViewModel("foreign", backend);
        await vm.InitializeAsync();
        vm.SelectedNode = Assert.Single(vm.Roots);
        vm.InspectionTabIndex = 1;
        var known = new BindingDetailValue(BindingDetailState.Known, "preview");
        var details = new BindingDetails("foreign", known, known with { ObjectId = 91 }, known, known, known, known, known,
            known, known, known, known, known, [], [], [], ResourceKey: new(BindingDetailState.Unavailable, Reason: "No real key"));
        var binding = new BindingDiagnosticInfo("Text", "Name", "Foreign", "Unknown", null, "Unknown", null, details);
        vm.BindingDiagnostics.Add(binding);
        var card = vm.CreateBindingDiagnosticCard(binding);
        var source = Assert.Single(card.Details!.Fields, row => row.Name == "来源对象");
        Assert.False(source.NavigateCommand!.CanExecute(null));
        Assert.Null(Assert.Single(card.Details.Fields, row => row.Name == "ResourceKey").NavigateCommand);
        vm.StyleFrames.Add(new("active", "Style", "Style", true, []));
        vm.StyleFrames.Add(new("inactive", "Style", "Style", false, []));
        vm.ActiveStyleFramesOnly = true;
        Assert.Single(vm.FilteredStyleFrames);
        Assert.Equal(2, vm.StyleFrames.Count);
        vm.RunningBindingsOnly = true;
        Assert.Empty(vm.FilteredBindingDiagnostics);
        Assert.Single(vm.BindingDiagnostics);
        vm.RunningBindingsOnly = false;
        vm.ErrorBindingsOnly = true;
        Assert.Empty(vm.FilteredBindingDiagnostics);
    }

    private static DiagnosticsAgent CreateAgent(Window target) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions { PipeName = "binding-nav-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });

    private static async Task SelectBindingAsync(MainWindowViewModel vm)
    {
        await vm.InitializeAsync();
        vm.SelectedNode = Assert.Single(Assert.Single(vm.Roots).Children, node => node.Node.Name == "BindingTarget");
        await WaitUntilAsync(() => vm.FilteredProperties.Count > 0);
        vm.InspectionTabIndex = 1;
        await WaitUntilAsync(() => vm.BindingDiagnostics.Any(binding => binding.PropertyName == "Text"));
    }

    private static BindingDiagnosticInfo TextBinding(MainWindowViewModel vm) => Assert.Single(vm.BindingDiagnostics, binding => binding.PropertyName == "Text");
    private static BindingDetailRowViewModel Field(MainWindowViewModel vm, string name) =>
        Assert.Single(vm.CreateBindingDiagnosticCard(TextBinding(vm)).Details!.Fields, field => field.Name == name);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference SetTransientDataContext(TextBlock control)
    {
        var model = new NavigationModel();
        control.DataContext = model;
        return new WeakReference(model);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    private sealed class NavigationModel
    {
        internal Action? OnRead { get; set; }
        public string Name => "Original";
        public NavigationModel Self => this;
        public NavigationChild Child { get; }
        public string AReentrantGetter { get { var callback = OnRead; OnRead = null; callback?.Invoke(); return "Getter"; } }
        public NavigationModel() => Child = new(this);
    }
    private sealed class NavigationChild(NavigationModel parent) { public NavigationModel Parent => parent; }
    private sealed class NavigationKey { public string Identity => "Actual key"; public override string ToString() => "NavigationAccent"; }

    private sealed class ForeignBackend : IInspectionBackend
    {
        public string BackendId => "foreign";
        public string DisplayName => "Foreign";
        public bool IsConnected { get; private set; }
        public bool HasStableObjectIds => false;
        public bool HasPushUpdates => false;
        public string? LastNotice => null;
        public SessionInfo? Session { get; private set; }
        public event Action? ConnectionChanged;
        public Task ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
        { IsConnected = true; Session = new("foreign", "Foreign", 0, "12.1.4", DiagnosticsProtocol.Version); ConnectionChanged?.Invoke(); return Task.CompletedTask; }
        public Task DisconnectAsync() { IsConnected = false; Session = null; ConnectionChanged?.Invoke(); return Task.CompletedTask; }
        public Task<IReadOnlyList<TreeNodeInfo>> GetRootsAsync(TreeKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TreeNodeInfo>>([new(1, "Foreign.Control", "Foreign", null, false)]);
        public Task<IReadOnlyList<TreeNodeInfo>> GetChildrenAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TreeNodeInfo>>([]);
        public Task<IReadOnlyList<PropertyInfoDto>> GetPropertiesAsync(long objectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PropertyInfoDto>>([]);
        public Task<StyleSnapshot> GetStylesAsync(long objectId, CancellationToken cancellationToken = default) => Task.FromResult(new StyleSnapshot([], [], [], "Foreign"));
        public Task<IReadOnlyList<TreeNodeInfo>> GetAncestorsAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ElementScreenshot> CaptureElementAsync(long objectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }
}
