using System.IO.Pipes;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class BreakpointSnapshotRaceTests
{
    [AvaloniaFact]
    public async Task Older_pull_snapshot_does_not_erase_a_new_hit_or_resurrect_a_removed_breakpoint()
    {
        var name = "breakpoint-snapshot-race-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var historyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHistory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = new BreakpointInfo(1, new BreakpointSpec(BreakpointKind.Event,
            "Avalonia.Controls.Button:Click", RemoveOnHit: true), 0, BreakpointState.Active);
        var hit = new BreakpointHit(1, 1, 1, 2, definition.Definition.MemberId, "Click", DateTimeOffset.UtcNow, true);
        await using var peer = new RpcPeer(pipe, async (method, _, token) =>
        {
            switch (method)
            {
                case Methods.SessionInfo: return new SessionInfo("snapshot-race", "Race", Environment.ProcessId, "12.1.4", 1);
                case Methods.TreeRoots: return new[] { new TreeNodeInfo(1, "Window", "Root", null, false) };
                case Methods.TreeChildren: return Array.Empty<TreeNodeInfo>();
                case Methods.Highlight: return new OperationResult();
                case BreakpointMethods.List: return new[] { definition };
                case BreakpointMethods.History:
                    historyEntered.TrySetResult();
                    await releaseHistory.Task.WaitAsync(token);
                    return new BreakpointHistory([], 0);
                default: throw new RpcException("unsupported", method);
            }
        });
        var connection = pipe.WaitForConnectionAsync();
        await using var viewModel = new MainWindowViewModel(name);
        var initialize = viewModel.InitializeAsync();
        await connection;
        peer.Start();
        await initialize;
        var refresh = viewModel.RefreshBreakpointsAsync();
        try
        {
            await historyEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await peer.SendNotificationAsync(BreakpointMethods.Hit, hit);
            await peer.SendNotificationAsync(BreakpointMethods.Changed, new BreakpointChangedNotification([]));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (viewModel.BreakpointHits.Count != 1) await Task.Delay(10, timeout.Token);
            releaseHistory.TrySetResult();
            await refresh;
            Assert.Equal(1, Assert.Single(viewModel.BreakpointHits).Sequence);
            Assert.Empty(viewModel.Breakpoints);
        }
        finally { releaseHistory.TrySetResult(); }
    }
}

