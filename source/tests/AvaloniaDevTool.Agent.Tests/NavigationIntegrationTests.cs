using System.IO.Pipes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class NavigationIntegrationTests
{
    [AvaloniaFact]
    public async Task Focus_following_selects_real_focus_and_can_be_stopped()
    {
        var first = new TextBox { Name = "FocusFirst" };
        var second = new TextBox { Name = "FocusSecond" };
        var window = new Window { Content = new StackPanel { Children = { first, second } } };
        window.Show();
        var name = "avdt-focus-" + Guid.NewGuid().ToString("N");
        await using var agent = DiagnosticsAgent.Attach(Application.Current!,
            new DiagnosticsAgentOptions
                { PipeName = name, TopLevelProvider = () => [window] });
        try
        {
            await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            await using var peer = new RpcPeer(pipe);
            peer.Start();
            var secondResult = await peer.InvokeAsync<TreeSearchResult>(ExplorationMethods.Search,
                new TreeSearchRequest("FocusSecond", TreeKind.Logical));
            var firstResult = await peer.InvokeAsync<TreeSearchResult>(ExplorationMethods.Search,
                new TreeSearchRequest("FocusFirst", TreeKind.Logical));
            var secondId = Assert.Single(secondResult.Matches).Id;
            var firstId = Assert.Single(firstResult.Matches).Id;
            var selectedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var selectedFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.NotificationReceived += (method, payload) =>
            {
                if (method != Methods.SelectionChanged)
                    return;
                var id = ProtocolJson.Read<SelectionChangedNotification>(payload).ObjectId;
                if (id == secondId)
                    selectedSecond.TrySetResult();
                if (id == firstId)
                    selectedFirst.TrySetResult();
            };
            await peer.InvokeAsync<OperationResult>(ExplorationMethods.FollowFocus, new FollowFocusRequest(true));
            Assert.True(second.Focus());
            await selectedSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await peer.InvokeAsync<OperationResult>(ExplorationMethods.FollowFocus, new FollowFocusRequest(false));
            var initialFirstWasSelected = selectedFirst.Task.IsCompleted;
            if (initialFirstWasSelected)
                selectedFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(first.Focus());
            await peer.InvokeAsync<SessionInfo>(Methods.SessionInfo, null);
            Assert.False(selectedFirst.Task.IsCompleted);
        }
        finally
        {
            window.Close();
        }
    }
}
