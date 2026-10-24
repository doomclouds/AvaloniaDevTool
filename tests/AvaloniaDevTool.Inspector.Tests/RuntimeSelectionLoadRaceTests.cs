using System.IO.Pipes;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class RuntimeSelectionLoadRaceTests
{
    [AvaloniaFact]
    public async Task Activating_runtime_during_pending_selection_reads_once_and_explicit_refresh_still_reads_again()
    {
        await using var server = await DelayedSelectionServer.CreateAsync();
        await using var vm = new MainWindowViewModel(server.PipeName) { SelectedTreeIndex = 1 };
        await vm.InitializeAsync();
        var child = Assert.Single(Assert.Single(vm.Roots).Children);
        vm.SelectedNode = child;
        await server.HighlightEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(vm.FilteredProperties);
        Assert.Equal(0, server.RuntimeReadCount);

        // The first runtime request is queued behind the pending highlight response.
        vm.InspectionTabIndex = 9;
        Assert.True(vm.RuntimeIsLoading);
        server.ReleaseHighlight.TrySetResult();
        await server.FirstRuntimeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => vm.Status.StartsWith("已选择", StringComparison.Ordinal));

        // The selection continuation has run to its next await before this UI turn resumes.
        server.ReleaseFirstRuntime.TrySetResult();
        await WaitUntilAsync(() => !vm.RuntimeIsLoading && vm.RuntimeRows.Count == 1);
        Assert.Equal(1, server.RuntimeReadCount);
        Assert.Equal(child.Id, vm.RuntimeObjectId);
        Assert.Equal("Loaded", Assert.Single(vm.RuntimeRows).Preview);

        await vm.RefreshRuntimeAsync();
        Assert.Equal(2, server.RuntimeReadCount);
        Assert.False(vm.RuntimeIsLoading);
        Assert.Equal("Loaded", Assert.Single(vm.RuntimeRows).Preview);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class DelayedSelectionServer : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly NamedPipeServerStream _pipe;
        private RpcPeer? _peer;
        private Task _running = Task.CompletedTask;
        private int _runtimeReadCount;
        public string PipeName { get; } = "runtime-selection-race-" + Guid.NewGuid().ToString("N");
        public int RuntimeReadCount => Volatile.Read(ref _runtimeReadCount);
        public TaskCompletionSource HighlightEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseHighlight { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstRuntimeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstRuntime { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private DelayedSelectionServer() => _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        public static Task<DelayedSelectionServer> CreateAsync()
        {
            var server = new DelayedSelectionServer();
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
                    return new SessionInfo("runtime-selection-race", "Runtime selection race", Environment.ProcessId,
                        "12.1.4", DiagnosticsProtocol.Version);
                case Methods.TreeRoots:
                    return new[] { new TreeNodeInfo(1, "Avalonia.Controls.Window", "RaceRoot", null, true) };
                case Methods.TreeChildren:
                    return ProtocolJson.Read<TreeChildrenRequest>(parameters).ObjectId == 1
                        ? new[] { new TreeNodeInfo(2, "Tests.DelayedControl", "RaceTarget", null, false) }
                        : Array.Empty<TreeNodeInfo>();
                case Methods.PropertiesGet:
                    return new[] { new PropertyInfoDto("Tests.DelayedControl:Text", "Text", "Tests.DelayedControl",
                        "System.String", "Value", "Local", true, false, false) };
                case Methods.Highlight:
                    if (ProtocolJson.Read<HighlightRequest>(parameters).ObjectId == 2)
                    {
                        HighlightEntered.TrySetResult();
                        await ReleaseHighlight.Task.WaitAsync(token);
                    }
                    return new OperationResult();
                case RuntimeInspectionMethods.Members:
                    var request = ProtocolJson.Read<RuntimeMembersRequest>(parameters);
                    if (Interlocked.Increment(ref _runtimeReadCount) == 1)
                    {
                        FirstRuntimeEntered.TrySetResult();
                        await ReleaseFirstRuntime.Task.WaitAsync(token);
                    }
                    return new RuntimeObjectSnapshot(request.ObjectId, "Tests.DelayedControl", request.Offset, 1,
                        [new RuntimeMemberInfo("Tests.DelayedControl:Property:Text", "Text", RuntimeMemberKind.Property,
                            "Tests.DelayedControl", "System.String", new RuntimeValueInfo("System.String", "Loaded", null, false, false))],
                        false, RuntimeCollectionKind.None);
                default:
                    throw new RpcException("unsupported", method);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            ReleaseHighlight.TrySetResult();
            ReleaseFirstRuntime.TrySetResult();
            if (_peer is not null) await _peer.DisposeAsync();
            await _pipe.DisposeAsync();
            try { await _running; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }
}
