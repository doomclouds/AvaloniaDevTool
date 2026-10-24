using System.IO.Pipes;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class SelectionRefreshRaceTests
{
    [AvaloniaFact]
    public async Task Legacy_tree_transport_falls_back_once_and_keeps_every_local_page_reachable()
    {
        await using var server = await DelayedServer.CreateAsync(false, false, childrenCount: 235);
        await using var viewModel = new MainWindowViewModel(server.PipeName) { SelectedTreeIndex = 1 };
        await viewModel.InitializeAsync();
        var root = Assert.Single(viewModel.Roots);
        Assert.Equal(100, root.Children.Count); Assert.True(root.HasMoreChildren); Assert.Equal(235, root.TotalChildrenCount);
        Assert.Contains("旧版 Agent", viewModel.TreeExpansionNotice);
        var loadedIds = root.Children.Select(child => child.Id).ToArray();
        server.ReplaceFirstChild = true;
        var changed = await Assert.ThrowsAsync<RpcException>(() => viewModel.LoadMoreChildrenAsync(root));
        Assert.Equal("tree_changed", changed.Code);
        Assert.Equal(loadedIds, root.Children.Select(child => child.Id));
        server.ReplaceFirstChild = false;
        await viewModel.LoadMoreChildrenAsync(root);
        Assert.Equal(200, root.Children.Count); Assert.True(root.HasMoreChildren);
        await viewModel.LoadMoreChildrenAsync(root);
        Assert.Equal(235, root.Children.Count); Assert.False(root.HasMoreChildren);
        Assert.Equal(235, root.Children.Select(child => child.Id).Distinct().Count());
        Assert.Equal(1, server.PageRequestCount);
        Assert.Equal(4, server.ChildrenRequestCount);
    }

    [AvaloniaFact]
    public async Task A_successful_native_paging_session_never_downgrades_a_later_unsupported_request()
    {
        await using var server = await DelayedServer.CreateAsync(false, false, childrenCount: 235, nativeFirstPage: true);
        await using var viewModel = new MainWindowViewModel(server.PipeName) { SelectedTreeIndex = 1 };
        await viewModel.InitializeAsync();
        var root = Assert.Single(viewModel.Roots);
        Assert.Equal(100, root.Children.Count); Assert.True(root.HasMoreChildren);
        var failure = await Assert.ThrowsAsync<RpcException>(() => viewModel.LoadMoreChildrenAsync(root));
        Assert.Equal("unsupported", failure.Code);
        Assert.Equal(100, root.Children.Count);
        Assert.Equal(2, server.PageRequestCount);
        Assert.Equal(0, server.ChildrenRequestCount);
    }

    [AvaloniaFact]
    public async Task Refresh_preserves_a_newer_manual_selection_made_while_roots_are_loading()
    {
        await using var server = await DelayedServer.CreateAsync(delaySecondRoots: true, revealChildrenAfterRefresh: false);
        await using var viewModel = new MainWindowViewModel(server.PipeName) { SelectedTreeIndex = 1 };
        await viewModel.InitializeAsync();
        var original = Assert.Single(Assert.Single(viewModel.Roots).Children);
        viewModel.RefreshCommand.Execute(null);
        await server.SecondRootsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.SelectedNode = original;
        Assert.Equal(2, viewModel.SelectedNode.Id);
        server.ReleaseSecondRoots.TrySetResult();
        await WaitUntilAsync(() => viewModel.RefreshCommand.CanExecute(null));
        Assert.NotNull(viewModel.SelectedNode);
        Assert.Equal(2, viewModel.SelectedNode.Id);
        Assert.NotSame(original, viewModel.SelectedNode);
    }

    [AvaloniaFact]
    public async Task Refresh_resumes_a_requested_navigation_after_replacing_its_tree()
    {
        await using var server = await DelayedServer.CreateAsync(delaySecondRoots: false, revealChildrenAfterRefresh: true);
        await using var viewModel = new MainWindowViewModel(server.PipeName) { SelectedTreeIndex = 1 };
        await viewModel.InitializeAsync();
        Assert.Empty(Assert.Single(viewModel.Roots).Children);
        viewModel.SelectedSearchResult = DelayedServer.Child;
        await server.AncestorsEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.RefreshCommand.Execute(null);
        Assert.False(viewModel.RefreshCommand.CanExecute(null));
        server.ReleaseAncestors.TrySetResult();
        await WaitUntilAsync(() => viewModel.RefreshCommand.CanExecute(null));
        await WaitUntilAsync(() => viewModel.SelectedNode?.Id == 2);
        Assert.Equal("RaceTarget", viewModel.SelectedNode!.Node.Name);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class DelayedServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly NamedPipeServerStream _pipe;
        private readonly bool _delaySecondRoots, _revealChildrenAfterRefresh;
        private RpcPeer? _peer;
        private Task _running = Task.CompletedTask;
        private int _rootsRequests, _childrenRequests;
        private int _pageRequests;
        private readonly int _childrenCount;
        private readonly bool _nativeFirstPage;
        public bool ReplaceFirstChild { get; set; }
        public int PageRequestCount => Volatile.Read(ref _pageRequests);
        public int ChildrenRequestCount => Volatile.Read(ref _childrenRequests);
        public string PipeName { get; } = "selection-race-" + Guid.NewGuid().ToString("N");
        private DelayedServer(bool delaySecondRoots, bool revealChildrenAfterRefresh, int childrenCount, bool nativeFirstPage)
        {
            _delaySecondRoots = delaySecondRoots;
            _revealChildrenAfterRefresh = revealChildrenAfterRefresh;
            _childrenCount = childrenCount;
            _nativeFirstPage = nativeFirstPage;
            _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        }
        public static TreeNodeInfo Root { get; } = new(1, "Avalonia.Controls.Window", "RaceRoot", null, true);
        public static TreeNodeInfo Child { get; } = new(2, "Avalonia.Controls.Button", "RaceTarget", "Target", false);
        public TaskCompletionSource SecondRootsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecondRoots { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AncestorsEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAncestors { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static Task<DelayedServer> CreateAsync(bool delaySecondRoots, bool revealChildrenAfterRefresh, int childrenCount = 1,
            bool nativeFirstPage = false)
        {
            var server = new DelayedServer(delaySecondRoots, revealChildrenAfterRefresh, childrenCount, nativeFirstPage);
            server._running = server.RunAsync();
            return Task.FromResult(server);
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
                case Methods.SessionInfo:
                    return new SessionInfo("selection-race", "Race test", Environment.ProcessId, "12.1.4", DiagnosticsProtocol.Version);
                case Methods.TreeRoots:
                    if (Interlocked.Increment(ref _rootsRequests) == 2 && _delaySecondRoots)
                    {
                        SecondRootsEntered.TrySetResult();
                        await ReleaseSecondRoots.Task.WaitAsync(token);
                    }
                    return new[] { Root };
                case Methods.TreeChildren:
                    var request = ProtocolJson.Read<TreeChildrenRequest>(parameters);
                    if (request.ObjectId != 1) return Array.Empty<TreeNodeInfo>();
                    var number = Interlocked.Increment(ref _childrenRequests);
                    return _revealChildrenAfterRefresh && number == 1 ? Array.Empty<TreeNodeInfo>() :
                        Enumerable.Range(0, _childrenCount).Select(index => index == 0 ? ReplaceFirstChild ?
                            new TreeNodeInfo(9999, "Avalonia.Controls.Button", "Replacement", null, false) : Child :
                            new TreeNodeInfo(index + 2, "Avalonia.Controls.Button", "LegacyChild" + index, null, false)).ToArray();
                case TreePagingMethods.Children:
                    if (Interlocked.Increment(ref _pageRequests) == 1 && _nativeFirstPage)
                        return new TreeChildrenPage(Enumerable.Range(0, 100).Select(index => index == 0 ? Child :
                            new TreeNodeInfo(index + 2, "Avalonia.Controls.Button", "PagedChild" + index, null, false)).ToArray(),
                            0, _childrenCount, true, "next-page");
                    throw new RpcException("unsupported", method);
                case ExplorationMethods.Ancestors:
                    AncestorsEntered.TrySetResult();
                    await ReleaseAncestors.Task.WaitAsync(token);
                    return new[] { Root, Child };
                case Methods.PropertiesGet:
                    return Array.Empty<PropertyInfoDto>();
                case Methods.Highlight:
                    return new OperationResult();
                default:
                    throw new RpcException("unsupported", method);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            ReleaseAncestors.TrySetResult();
            ReleaseSecondRoots.TrySetResult();
            if (_peer is not null) await _peer.DisposeAsync();
            await _pipe.DisposeAsync();
            try { await _running; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}

