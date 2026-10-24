using System.Collections;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class RuntimeBrowserTests
{
    [AvaloniaFact]
    public async Task Control_datacontext_nested_cycle_and_getter_errors_are_readonly_and_navigable()
    {
        var model = new RuntimeModel();
        var control = new TextBlock { Name = "RuntimeTarget", Text = "Runtime", DataContext = model };
        var target = new Window { Content = control };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            vm.SelectedNode = Assert.Single(vm.Roots).Children.Single(node => node.Node.Name == "RuntimeTarget");
            await WaitUntilAsync(() => vm.FilteredProperties.Count > 0);
            vm.InspectionTabIndex = 9;
            await WaitUntilAsync(() => !vm.RuntimeIsLoading && vm.RuntimeRows.Count > 0);
            Assert.True(vm.RuntimeNextPageCommand.CanExecute(null));
            vm.RuntimeNextPageCommand.Execute(null);
            await WaitUntilAsync(() => !vm.RuntimeIsLoading && vm.RuntimePageText.Contains("第 2 页", StringComparison.Ordinal));
            await vm.RefreshRuntimeAsync();
            Assert.Contains("第 2 页", vm.RuntimePageText);
            vm.RuntimePreviousPageCommand.Execute(null);
            await WaitUntilAsync(() => !vm.RuntimeIsLoading && vm.RuntimeRows.Any(row => row.Name == "DataContext"));
            await EnterDataContextAsync(vm);
            var modelId = vm.RuntimeObjectId;
            Assert.Equal(2, vm.RuntimeBreadcrumbs.Count);
            var self = Row(vm, nameof(RuntimeModel.Self));
            Assert.True(self.IsSelfReference);
            Assert.Contains("自身引用", self.Flags);
            await OpenAsync(vm, self);
            Assert.Equal(modelId, vm.RuntimeObjectId);
            Assert.Equal(2, vm.RuntimeBreadcrumbs.Count);
            var nullRow = Row(vm, nameof(RuntimeModel.Null));
            Assert.True(nullRow.IsNull);
            Assert.False(nullRow.HasError);
            var error = Row(vm, nameof(RuntimeModel.Throwing));
            Assert.True(error.HasError);
            Assert.False(error.IsNull);
            Assert.Contains("InvalidOperationException", error.Error);
            Assert.False(error.OpenCommand.CanExecute(null));
            Assert.True(Row(vm, nameof(RuntimeModel.LongText)).Truncated);
            Assert.True(Row(vm, nameof(RuntimeModel.LongError)).ErrorTruncated);
            Assert.Contains("错误信息已截断", Row(vm, nameof(RuntimeModel.LongError)).Flags);
            Assert.Contains(vm.RuntimeRows, row => row.Name == nameof(RuntimeModel.PublicField) && row.Description.Contains("Field", StringComparison.Ordinal));
            await OpenAsync(vm, Row(vm, nameof(RuntimeModel.Child)));
            var parent = Row(vm, nameof(RuntimeChild.Parent));
            Assert.True(parent.IsCycleReference);
            Assert.False(parent.IsSelfReference);
            Assert.Contains("循环引用", parent.Flags);
            await OpenAsync(vm, parent);
            Assert.Equal(modelId, vm.RuntimeObjectId);
            Assert.Equal(2, vm.RuntimeBreadcrumbs.Count);
            model.Name = "Updated business data";
            await vm.RefreshRuntimeAsync();
            Assert.Contains(model.Name, Row(vm, nameof(RuntimeModel.Name)).Preview);
            vm.RuntimeBackCommand.Execute(null);
            await WaitUntilAsync(() => vm.RuntimeObjectId == vm.SelectedNode!.Id && !vm.RuntimeIsLoading);
            Assert.Single(vm.RuntimeBreadcrumbs);
            Assert.Contains(vm.RuntimeRows, row => row.Name == "DataContext");
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task List_and_dictionary_pages_keep_indices_keys_nulls_and_navigation_offsets()
    {
        var model = new RuntimeModel();
        var target = new Window { Content = new TextBlock { Name = "RuntimeTarget", DataContext = model } };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            await EnterDataContextAsync(vm);
            var modelId = vm.RuntimeObjectId;
            await OpenAsync(vm, Row(vm, nameof(RuntimeModel.Items)));
            Assert.Equal(1, vm.RuntimeSectionIndex);
            Assert.Equal(50, vm.RuntimeRows.Count);
            Assert.Equal("[0]", vm.RuntimeRows[0].Name);
            Assert.True(vm.RuntimeRows[0].IsNull);
            Assert.True(vm.RuntimeRows[2].IsCycleReference);
            Assert.True(vm.RuntimeRows[3].IsSelfReference);
            Assert.True(vm.RuntimeNextPageCommand.CanExecute(null));
            vm.RuntimeNextPageCommand.Execute(null);
            await WaitUntilAsync(() => !vm.RuntimeIsLoading && vm.RuntimeRows.FirstOrDefault()?.Name == "[50]");
            Assert.Contains("第 2 页", vm.RuntimePageText);
            vm.RuntimeNextPageCommand.Execute(null);
            await WaitUntilAsync(() => !vm.RuntimeIsLoading && vm.RuntimeRows.FirstOrDefault()?.Name == "[100]");
            Assert.Equal(21, vm.RuntimeRows.Count);
            Assert.False(vm.RuntimeNextPageCommand.CanExecute(null));
            vm.RuntimePreviousPageCommand.Execute(null);
            await WaitUntilAsync(() => !vm.RuntimeIsLoading && vm.RuntimeRows.FirstOrDefault()?.Name == "[50]");
            await vm.RefreshRuntimeAsync();
            Assert.Equal("[50]", vm.RuntimeRows[0].Name);
            var listId = vm.RuntimeObjectId;
            await OpenAsync(vm, vm.RuntimeRows[0]);
            Assert.Contains(nameof(RuntimeChild), vm.RuntimeTitle);
            vm.RuntimeBackCommand.Execute(null);
            await WaitUntilAsync(() => vm.RuntimeObjectId == listId && !vm.RuntimeIsLoading);
            Assert.Equal("[50]", vm.RuntimeRows[0].Name);
            vm.RuntimeBackCommand.Execute(null);
            await WaitUntilAsync(() => vm.RuntimeObjectId == modelId && !vm.RuntimeIsLoading);
            await OpenAsync(vm, Row(vm, nameof(RuntimeModel.Map)));
            Assert.All(vm.RuntimeRows, row => Assert.True(row.HasKey));
            Assert.Equal("key-000", vm.RuntimeRows[0].KeyPreview.Trim('"'));
            vm.RuntimeNextPageCommand.Execute(null);
            await WaitUntilAsync(() => !vm.RuntimeIsLoading && vm.RuntimeRows.FirstOrDefault()?.Name == "[50]");
            Assert.Equal("key-050", vm.RuntimeRows[0].KeyPreview.Trim('"'));
            vm.RuntimeNextPageCommand.Execute(null);
            await WaitUntilAsync(() => !vm.RuntimeIsLoading && vm.RuntimeRows.FirstOrDefault()?.Name == "[100]");
            Assert.Equal(21, vm.RuntimeRows.Count);
            Assert.False(vm.RuntimeNextPageCommand.CanExecute(null));
            vm.RuntimeBackCommand.Execute(null);
            await WaitUntilAsync(() => vm.RuntimeObjectId == modelId && !vm.RuntimeIsLoading);
            await OpenAsync(vm, Row(vm, nameof(RuntimeModel.ObjectKeys)));
            var keyed = Assert.Single(vm.RuntimeRows);
            Assert.NotNull(keyed.Key!.ValueObjectId);
            Assert.True(keyed.OpenKeyCommand.CanExecute(null));
            keyed.OpenKeyCommand.Execute(null);
            await WaitUntilAsync(() => vm.RuntimeObjectId == keyed.Key.ValueObjectId && !vm.RuntimeIsLoading);
            Assert.Contains(nameof(RuntimeChild), vm.RuntimeTitle);
            Assert.True(Row(vm, nameof(RuntimeChild.Parent)).IsCycleReference);
            vm.RuntimeUseSelectionCommand.Execute(null);
            await WaitUntilAsync(() => vm.RuntimeObjectId == vm.SelectedNode!.Id && !vm.RuntimeIsLoading);
            Assert.Single(vm.RuntimeBreadcrumbs);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Enumerable_only_and_expired_temporary_values_do_not_appear_as_empty_or_null()
    {
        var model = new RuntimeModel();
        var target = new Window { Content = new TextBlock { Name = "RuntimeTarget", DataContext = model } };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            await EnterDataContextAsync(vm);
            var modelId = vm.RuntimeObjectId;
            await OpenAsync(vm, Row(vm, nameof(RuntimeModel.Deferred)));
            vm.RuntimeSectionIndex = 1;
            await WaitUntilAsync(() => !vm.RuntimeIsLoading && vm.RuntimeSummary.Contains("IEnumerable", StringComparison.Ordinal));
            Assert.False(vm.RuntimeCollectionSupported);
            Assert.Equal(0, model.Deferred.EnumerationCount);
            Assert.Contains("不枚举", vm.RuntimeSummary);
            vm.RuntimeBackCommand.Execute(null);
            await WaitUntilAsync(() => vm.RuntimeObjectId == modelId && !vm.RuntimeIsLoading);
            var temporary = Row(vm, nameof(RuntimeModel.Temporary));
            Assert.False(temporary.IsNull);
            Assert.NotNull(temporary.ValueObjectId);
            CollectTemporary();
            Assert.False(RuntimeModel.LastTemporary!.IsAlive);
            await OpenAsync(vm, temporary);
            Assert.True(vm.RuntimeHasError);
            Assert.Contains("object_expired", vm.RuntimeError);
            Assert.Contains("重试", vm.RuntimeError);
            Assert.Contains("不能据此判断", vm.RuntimeSummary);
            Assert.Contains("读取失败", vm.RuntimePageText);
            Assert.DoesNotContain("0 / 0", vm.RuntimePageText);
            Assert.Contains(temporary.ValueObjectId!.Value.ToString(), vm.RuntimeTitle);
            Assert.Empty(vm.RuntimeRows);
            Assert.True(vm.RuntimeBackCommand.CanExecute(null));
            Assert.All(vm.RuntimeBreadcrumbs, crumb => Assert.True(crumb.OpenCommand.CanExecute(null)));
            vm.RuntimeBreadcrumbs[^2].OpenCommand.Execute(null);
            await WaitUntilAsync(() => vm.RuntimeObjectId == modelId && !vm.RuntimeIsLoading);
            Assert.False(vm.RuntimeHasError);
            Assert.NotNull(Row(vm, nameof(RuntimeModel.Temporary)).ValueObjectId);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Real_agent_late_response_cannot_replace_a_new_selection_or_a_hidden_page()
    {
        var first = new ReentrantControl { Name = "RuntimeTarget" };
        var second = new TextBlock { Name = "SecondTarget", Text = "New selection" };
        var target = new Window { Content = new StackPanel { Name = "RuntimePanel", Children = { first, second } } };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            var panel = Assert.Single(vm.Roots).Children.Single(node => node.Node.Name == "RuntimePanel");
            panel.IsExpanded = true;
            await WaitUntilAsync(() => panel.Children.Count == 2);
            var firstNode = panel.Children.Single(node => node.Node.Name == "RuntimeTarget");
            var secondNode = panel.Children.Single(node => node.Node.Name == "SecondTarget");
            vm.SelectedNode = firstNode;
            await WaitUntilAsync(() => vm.FilteredProperties.Count > 0);
            Assert.Equal(0, first.ReadCount);
            first.OnRead = () => vm.SelectedNode = secondNode;
            vm.InspectionTabIndex = 9;
            await WaitUntilAsync(() => vm.RuntimeObjectId == secondNode.Id && !vm.RuntimeIsLoading && vm.RuntimeRows.Count > 0);
            Assert.Equal(secondNode.Id, vm.SelectedNode!.Id);
            Assert.Equal(1, first.ReadCount);
            Assert.DoesNotContain(vm.RuntimeRows, row => row.Name == nameof(ReentrantControl.AReentrantGetter));
            vm.InspectionTabIndex = 0;
            vm.SelectedNode = firstNode;
            await WaitUntilAsync(() => vm.FilteredProperties.Count > 0);
            Assert.Equal(1, first.ReadCount);
            first.OnRead = () => vm.InspectionTabIndex = 0;
            vm.InspectionTabIndex = 9;
            await WaitUntilAsync(() => first.ReadCount == 2 && vm.InspectionTabIndex == 0);
            await Task.Delay(60);
            Assert.Empty(vm.RuntimeRows);
            Assert.False(vm.RuntimeIsLoading);
            first.OnRead = null;
            vm.InspectionTabIndex = 9;
            await WaitUntilAsync(() => !vm.RuntimeIsLoading && vm.RuntimeRows.Any(row => row.Name == nameof(ReentrantControl.AReentrantGetter)));
            Assert.Equal(3, first.ReadCount);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Disconnect_and_dispose_clear_paths_rows_and_disable_retained_commands()
    {
        var model = new RuntimeModel();
        var control = new ReentrantControl { Name = "RuntimeTarget", DataContext = model };
        var target = new Window { Content = control };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            await EnterDataContextAsync(vm);
            var oldRow = Row(vm, nameof(RuntimeModel.Child));
            var oldCrumb = vm.RuntimeBreadcrumbs[0];
            control.OnRead = () => vm.DisconnectCommand.Execute(null);
            vm.RuntimeUseSelectionCommand.Execute(null);
            await WaitUntilAsync(() => !vm.IsConnected && vm.RuntimeBreadcrumbs.Count == 0);
            Assert.Empty(vm.RuntimeRows);
            Assert.Null(vm.RuntimeObjectId);
            Assert.False(vm.RefreshRuntimeCommand.CanExecute(null));
            Assert.False(oldRow.OpenCommand.CanExecute(null));
            Assert.False(oldCrumb.OpenCommand.CanExecute(null));
            var reconnectEntry = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnReconnectRootPublished(object? sender, NotifyCollectionChangedEventArgs change)
            {
                if (change.Action == NotifyCollectionChangedAction.Add && !reconnectEntry.Task.IsCompleted)
                {
                    // Enter immediately when the root is published, before lazy children have loaded.
                    reconnectEntry.TrySetResult(EnterDataContextAsync(vm));
                }
            }
            vm.Roots.CollectionChanged += OnReconnectRootPublished;
            try
            {
                vm.ConnectCommand.Execute(null);
                await (await reconnectEntry.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            }
            finally { vm.Roots.CollectionChanged -= OnReconnectRootPublished; }
            Assert.True(vm.IsConnected);
            Assert.False(oldRow.OpenCommand.CanExecute(null));
            Assert.False(oldCrumb.OpenCommand.CanExecute(null));
            await vm.DisposeAsync();
            Assert.Empty(vm.RuntimeRows);
            Assert.Empty(vm.RuntimeBreadcrumbs);
            Assert.Null(vm.RuntimeObjectId);
            Assert.False(vm.RefreshRuntimeCommand.CanExecute(null));
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Markup_exposes_tenth_native_tab_and_external_backend_cannot_read_runtime()
    {
        await using var backend = new ForeignBackend();
        await using var vm = new MainWindowViewModel("foreign", backend);
        await vm.InitializeAsync();
        Assert.Equal(2, vm.TreeKinds.Count);
        vm.SelectedTreeIndex = 2;
        Assert.Equal(0, vm.SelectedTreeIndex);
        vm.SelectedNode = Assert.Single(vm.Roots);
        vm.InspectionTabIndex = 9;
        await vm.RefreshRuntimeAsync();
        Assert.Empty(vm.RuntimeRows);
        Assert.False(vm.RefreshRuntimeCommand.CanExecute(null));
        var window = new MainWindow { DataContext = vm };
        try
        {
            var tabs = window.FindControl<TabControl>("InspectionTabs")!.Items.OfType<TabItem>().ToArray();
            Assert.Equal("对象", tabs[9].Header);
            Assert.False(tabs[9].IsEnabled);
            var view = new RuntimeView { DataContext = vm };
            Assert.NotNull(view.FindControl<ListBox>("RuntimeList"));
        }
        finally { window.Close(); await window.ViewModel.DisposeAsync(); }
    }

    private static DiagnosticsAgent CreateAgent(Window window) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions
        {
            PipeName = "runtime-browser-tests-" + Guid.NewGuid().ToString("N"),
            ApplicationName = "Runtime browser tests", TopLevelProvider = () => [window]
        });

    private static async Task EnterDataContextAsync(MainWindowViewModel vm)
    {
        await WaitUntilAsync(() => vm.IsConnected && vm.Roots.Count == 1 &&
            vm.Roots[0].Children.Any(node => !node.IsPlaceholder && node.Node.Name == "RuntimeTarget"));
        var root = Assert.Single(vm.Roots);
        vm.SelectedNode = root.Children.Single(node => node.Node.Name == "RuntimeTarget");
        await WaitUntilAsync(() => vm.FilteredProperties.Count > 0);
        vm.InspectionTabIndex = 9;
        await WaitUntilAsync(() => !vm.RuntimeIsLoading && vm.RuntimeRows.Any(row => row.Name == "DataContext"));
        await OpenAsync(vm, Row(vm, "DataContext"));
        Assert.Contains(nameof(RuntimeModel), vm.RuntimeTitle);
    }

    private static RuntimeRowViewModel Row(MainWindowViewModel vm, string name) => Assert.Single(vm.RuntimeRows, row => row.Name == name);

    private static async Task OpenAsync(MainWindowViewModel vm, RuntimeRowViewModel row)
    {
        Assert.True(row.OpenCommand.CanExecute(null));
        row.OpenCommand.Execute(null);
        await WaitUntilAsync(() => vm.RuntimeObjectId == row.ValueObjectId && !vm.RuntimeIsLoading && (vm.RuntimeRows.Count > 0 || vm.RuntimeHasError));
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CollectTemporary()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class RuntimeModel
    {
        public static WeakReference? LastTemporary { get; private set; }
        public string Name { get; set; } = "Original";
        public RuntimeModel Self => this;
        public RuntimeChild Child { get; }
        public IList Items { get; }
        public IDictionary Map { get; }
        public IDictionary ObjectKeys { get; }
        public EnumerableOnly Deferred { get; } = new();
        public string? Null => null;
        public string LongText => new('x', 2_100);
        public string Throwing => throw new InvalidOperationException("One member failed");
        public string LongError => throw new InvalidOperationException(new string('x', 2_100));
        public readonly string PublicField = "Field value";
        public object Temporary
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get
            {
                var value = new RuntimeChild(this);
                LastTemporary = new WeakReference(value);
                return value;
            }
        }

        public RuntimeModel()
        {
            Child = new RuntimeChild(this);
            var items = new ArrayList { null, Child, this };
            items.Add(items);
            for (var i = 4; i < 121; i++) items.Add(i);
            items[50] = Child;
            Items = items;
            var map = new SortedList();
            for (var i = 0; i < 121; i++) map.Add($"key-{i:000}", i);
            Map = map;
            ObjectKeys = new Hashtable { [Child] = "Complex key" };
        }
    }

    private sealed class RuntimeChild(RuntimeModel parent)
    {
        public RuntimeModel Parent { get; } = parent;
        public string Name => "Nested";
    }

    private sealed class EnumerableOnly : IEnumerable
    {
        public int EnumerationCount { get; private set; }
        public IEnumerator GetEnumerator()
        {
            EnumerationCount++;
            throw new InvalidOperationException("Do not enumerate");
        }
    }

    private sealed class ReentrantControl : TextBlock
    {
        internal Action? OnRead { get; set; }
        internal int ReadCount { get; private set; }
        public string AReentrantGetter
        {
            get
            {
                ReadCount++;
                var action = OnRead;
                OnRead = null;
                action?.Invoke();
                return "Old object getter";
            }
        }
    }

    private sealed class ForeignBackend : IInspectionBackend
    {
        public string BackendId => "foreign-test";
        public string DisplayName => "Foreign test";
        public bool IsConnected { get; private set; }
        public bool HasStableObjectIds => false;
        public bool HasPushUpdates => false;
        public string? LastNotice => null;
        public SessionInfo? Session { get; private set; }
        public event Action? ConnectionChanged;
        public Task ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
        {
            IsConnected = true;
            Session = new("foreign", "Foreign test", Environment.ProcessId, "12.1.4", DiagnosticsProtocol.Version);
            ConnectionChanged?.Invoke();
            return Task.CompletedTask;
        }
        public Task DisconnectAsync()
        {
            IsConnected = false;
            Session = null;
            ConnectionChanged?.Invoke();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<TreeNodeInfo>> GetRootsAsync(TreeKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TreeNodeInfo>>([new(1, "Foreign.Control", "Foreign", null, false)]);
        public Task<IReadOnlyList<TreeNodeInfo>> GetChildrenAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TreeNodeInfo>>([]);
        public Task<IReadOnlyList<PropertyInfoDto>> GetPropertiesAsync(long objectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PropertyInfoDto>>([]);
        public Task<StyleSnapshot> GetStylesAsync(long objectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<TreeNodeInfo>> GetAncestorsAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ElementScreenshot> CaptureElementAsync(long objectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async ValueTask DisposeAsync() => await DisconnectAsync();
    }
}
