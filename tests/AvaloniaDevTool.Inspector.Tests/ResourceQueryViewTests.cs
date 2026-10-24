using System.IO.Pipes;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class ResourceQueryViewTests
{
    [AvaloniaFact]
    public async Task Resource_pages_filters_and_nested_edits_use_the_original_dictionary()
    {
        var merged = new ResourceDictionary { ["Duplicate"] = 7 };
        var control = new Border { Name = "PagedResourceTarget", Resources = new ResourceDictionary
        { ["Duplicate"] = 3, MergedDictionaries = { merged } } };
        for (var i = 0; i < 120; i++) control.Resources["Key" + i.ToString("D3")] = i;
        var target = new Window { Content = control };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            vm.SelectedNode = Assert.Single(Assert.Single(vm.Roots).Children, node => node.Node.Name == "PagedResourceTarget");
            await WaitUntilAsync(() => vm.FilteredProperties.Count > 0);
            vm.InspectionTabIndex = 2;
            await WaitUntilAsync(() => vm.ResourceDictionaries.Any(item => item.Name.Contains("PagedResourceTarget", StringComparison.Ordinal)));
            var selected = Assert.Single(vm.ResourceDictionaries, item => item.Name.Contains("PagedResourceTarget", StringComparison.Ordinal));
            vm.SelectedResourceDictionary = selected;
            await WaitUntilAsync(() => vm.ResourcePageSummary == "1–50 / 121 项");
            Assert.True(vm.NextResourcePageCommand.CanExecute(null));
            Assert.False(vm.PreviousResourcePageCommand.CanExecute(null));
            vm.NextResourcePageCommand.Execute(null);
            await WaitUntilAsync(() => vm.ResourcePageSummary == "51–100 / 121 项");
            vm.ResourceKeyFilter = "Key119";
            await WaitUntilAsync(() => vm.ResourceEntries.Count == 1 && vm.ResourceEntries[0].Key == "Key119");
            Assert.Equal("1–1 / 1 项", vm.ResourcePageSummary);
            Assert.Same(selected, vm.SelectedResourceDictionary);
            vm.ResourceKeyFilter = "Duplicate";
            vm.IncludeNestedResources = true;
            await WaitUntilAsync(() => vm.ResourceEntries.Count == 2);
            var row = Assert.Single(vm.ResourceEntries, entry => entry.DictionaryId != selected.Id);
            Assert.Contains("Merged", row.Provenance);
            row.Draft = "24";
            row.ApplyCommand.Execute(null);
            await WaitUntilAsync(() => Equals(merged["Duplicate"], 24) && row.IsOverridden);
            Assert.Equal(3, control.Resources["Duplicate"]);
            merged["Duplicate"] = 25;
            row.ReadCommand!.Execute(null);
            await WaitUntilAsync(() => row.Draft == "25");
            Assert.False(row.IsOverridden);
            vm.DisconnectCommand.Execute(null);
            await WaitUntilAsync(() => !vm.IsConnected && vm.ResourceEntries.Count == 0);
            Assert.Equal("尚未查询", vm.ResourcePageSummary);
            Assert.False(vm.NextResourcePageCommand.CanExecute(null));
            Assert.Equal(25, merged["Duplicate"]);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Axaml_exposes_nested_filter_sort_page_controls_and_provider_provenance()
    {
        var target = new Window { Content = new Border() };
        target.Show();
        await using var agent = CreateAgent(target);
        await using var vm = new MainWindowViewModel(agent.PipeName);
        await vm.InitializeAsync();
        var row = new ResourceEntryViewModel(new ResourceQueryEntryInfo(3, "Application.Resources/Merged[0]", "Application", "Dark",
            "Example", "System.String", "Example", 4, "System.Int32", "12", null, true, false, false, false),
            _ => Task.CompletedTask, _ => Task.CompletedTask);
        vm.ResourceEntries.Add(row);
        var view = new ResourcesView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 800, Content = view };
        window.Show();
        try
        {
            foreach (var id in new[] { "Inspector.IncludeNestedResources", "Inspector.ResourceKeyFilter", "Inspector.ResourceTypeFilter",
                "Inspector.ResourceSort", "Inspector.ResourceSortDescending", "Inspector.PreviousResourcePage", "Inspector.NextResourcePage" })
                Assert.Single(view.GetVisualDescendants().OfType<Control>(), control => AutomationProperties.GetAutomationId(control) == id);
            var provenance = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(),
                control => AutomationProperties.GetAutomationId(control) == "Inspector.ResourceProvenance");
            Assert.Contains("Application.Resources/Merged[0]", provenance.Text);
            Assert.Contains("Dark", provenance.Text);
        }
        finally { window.Close(); target.Close(); }
    }

    [AvaloniaFact]
    public async Task Late_page_after_filter_or_dictionary_change_cannot_replace_current_rows()
    {
        await using var server = new DelayedResourceServer();
        await using var vm = new MainWindowViewModel(server.PipeName);
        await vm.InitializeAsync();
        vm.InspectionTabIndex = 2;
        await server.FirstPageEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.ResourceKeyFilter = "Latest";
        vm.SelectedResourceDictionary = Assert.Single(vm.ResourceDictionaries, dictionary => dictionary.Id == 20);
        server.ReleaseFirstPage.TrySetResult();
        await WaitUntilAsync(() => vm.ResourceEntries.Count == 1 && vm.ResourceEntries[0].Key == "Latest20");
        Assert.Equal(20, vm.SelectedResourceDictionary!.Id);
        Assert.Equal("1–1 / 1 项", vm.ResourcePageSummary);
        Assert.DoesNotContain(vm.ResourceEntries, row => row.Key == "Old10");
        Assert.Equal("Latest", server.LastQuery!.KeyFilter);
        vm.DisconnectCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsConnected && vm.ResourceEntries.Count == 0 && vm.ResourceDictionaries.Count == 0);
        Assert.Empty(vm.ResourceEntries);
        Assert.Equal("尚未查询", vm.ResourcePageSummary);
    }

    [AvaloniaFact]
    public async Task Late_page_after_disconnect_does_not_restore_resource_state()
    {
        await using var server = new DelayedResourceServer();
        await using var vm = new MainWindowViewModel(server.PipeName);
        await vm.InitializeAsync();
        vm.InspectionTabIndex = 2;
        await server.FirstPageEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.DisconnectCommand.Execute(null);
        await WaitUntilAsync(() => !vm.IsConnected && vm.ResourceDictionaries.Count == 0);
        server.ReleaseFirstPage.TrySetResult();
        await server.FirstPageReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Yield();
        Assert.Empty(vm.ResourceEntries);
        Assert.Empty(vm.ResourceDictionaries);
        Assert.Null(vm.SelectedResourceDictionary);
        Assert.Equal("尚未查询", vm.ResourcePageSummary);
    }

    private static DiagnosticsAgent CreateAgent(Window window) => DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
    { PipeName = "resource-view-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class DelayedResourceServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly NamedPipeServerStream _pipe;
        private readonly Task _run;
        private RpcPeer? _peer;
        private int _pageCount;
        public string PipeName { get; } = "resource-race-" + Guid.NewGuid().ToString("N");
        public TaskCompletionSource FirstPageEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstPage { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstPageReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ResourceEntriesQueryRequest? LastQuery { get; private set; }
        public DelayedResourceServer()
        {
            _pipe = new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _run = RunAsync();
        }
        private async Task RunAsync()
        {
            await _pipe.WaitForConnectionAsync(_stop.Token);
            await using var peer = new RpcPeer(_pipe, HandleAsync);
            _peer = peer;
            peer.Start();
            await peer.Completion;
        }
        private async Task<object?> HandleAsync(string method, JsonElement parameters, CancellationToken token)
        {
            switch (method)
            {
                case Methods.SessionInfo: return new SessionInfo("resource-race", "Resources", 1, "12.1.4", DiagnosticsProtocol.Version);
                case Methods.TreeRoots: return new[] { new TreeNodeInfo(1, "Avalonia.Controls.Window", "Root", null, false) };
                case ResourceQueryMethods.Trees:
                    return new ResourceTreeSnapshot([Provider(10), Provider(20)], 2, false, []);
                case ResourceQueryMethods.Entries:
                    var query = ProtocolJson.Read<ResourceEntriesQueryRequest>(parameters);
                    LastQuery = query;
                    var first = Interlocked.Increment(ref _pageCount) == 1;
                    if (first)
                    {
                        FirstPageEntered.TrySetResult();
                        await ReleaseFirstPage.Task;
                        FirstPageReturned.TrySetResult();
                    }
                    var key = (first ? "Old" : "Latest") + query.DictionaryId;
                    return new ResourceEntriesSnapshot(query.DictionaryId, 0, 1,
                        [new(query.DictionaryId, "Dictionary" + query.DictionaryId, "Application", null, key, "System.String", key,
                            query.DictionaryId + 1, "System.Int32", "7", null, true, false, false, false)], false, false, 1, 1, []);
                default: return new OperationResult();
            }
        }
        private static ResourceProviderInfo Provider(long id) => new(id, "Dictionary" + id, "Application", null,
            "Dictionary" + id, "Avalonia.Controls.ResourceDictionary", 1, true, false, false, []);
        public async ValueTask DisposeAsync()
        {
            ReleaseFirstPage.TrySetResult();
            _stop.Cancel();
            if (_peer is not null) await _peer.DisposeAsync();
            await _pipe.DisposeAsync();
            try { await _run; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
