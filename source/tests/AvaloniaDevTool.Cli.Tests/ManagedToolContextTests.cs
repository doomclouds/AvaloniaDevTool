using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class ManagedToolContextTests
{
    [Fact]
    public async Task Host_generations_are_unique_when_sessions_allocate_concurrently()
    {
        var allocator = new NodeGenerationAllocator();
        var allocations = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            Enumerable.Range(0, 256).Select(_ => allocator.Allocate()).ToArray())));
        var generations = allocations.SelectMany(values => values).Order().ToArray();

        Assert.Equal(Enumerable.Range(1, generations.Length).Select(value => (long)value), generations);
    }

    [Fact]
    public void Exhausted_host_generation_budget_never_wraps_or_recovers()
    {
        var allocator = new NodeGenerationAllocator();
        long last = 0;
        for (var index = 0; index < NodeGenerationAllocator.MaximumGeneration; index++)
            last = allocator.Allocate();

        Assert.Equal(NodeGenerationAllocator.MaximumGeneration, last);
        Assert.Contains("session_limit", Assert.Throws<McpException>(() => allocator.Allocate()).Message);
        Assert.Contains("session_limit", Assert.Throws<McpException>(() => allocator.Allocate()).Message);
    }

    [Fact]
    public async Task Managed_gate_cancellation_and_errors_do_not_block_subsequent_calls()
    {
        await using var session = new ToolSession(null);
        var context = new ManagedToolContext(session);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = context.WithSessionAsync(CancellationToken.None,
            async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
                return 1;
            });
        await entered.Task;
        using var cancellation = new CancellationTokenSource();
        var invoked = false;
        var waiting = context.WithSessionAsync(cancellation.Token,
            (_, _) =>
            {
                invoked = true;
                return Task.FromResult(2);
            });
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally
        {
            release.SetResult();
        }

        Assert.Equal(1, await first);
        Assert.False(invoked);

        var rpc = await Assert.ThrowsAsync<McpException>(() => context.WithSessionAsync<int>(CancellationToken.None,
            (_, _) => throw new RpcException("test_rpc", "Expected RPC failure.")));
        Assert.Contains("test_rpc: Expected RPC failure.", rpc.Message);
        var argument = await Assert.ThrowsAsync<McpException>(() => context.WithSessionAsync<int>(
            CancellationToken.None,
            (_, _) => throw new ArgumentException("Expected argument failure.")));
        Assert.Contains("invalid_argument: Expected argument failure.", argument.Message);
        Assert.Equal(3, await context.WithSessionAsync(CancellationToken.None, (_, _) => Task.FromResult(3)));
    }

    [AvaloniaFact]
    public async Task Shared_host_allocator_rejects_previous_sessions_nodes_and_preserves_shared_tool_identity()
    {
        using var fixture = new TargetFixture();
        await using var agent = fixture.Attach();
        var allocator = new NodeGenerationAllocator();
        await using var firstSession = new ToolSession(agent.PipeName);
        var firstContext = new ManagedToolContext(firstSession, allocator);
        var firstOfficial = new StructureTools(firstSession, firstContext);
        var firstNode = Assert.Single((await firstOfficial.Search(fixture.Button.Name!, searchTemplates: true)).Matches)
            .Id;
        var firstTools = new FrameworkInteractionTools(firstSession, firstContext);
        var clicks = 0;
        fixture.Button.Click += (_, _) => clicks++;
        Assert.True((await firstTools.UiClick(firstNode)).Delivered);
        Assert.True((await new FrameworkInteractionTools(firstSession, firstContext).UiClick(firstNode)).Delivered);
        Assert.Equal(2, clicks);
        var waited = await firstTools.UiWait(fixture.Button.Name!,
            "Equals",
            "Avalonia.Controls.ContentControl:Content",
            "Managed target");
        Assert.Equal(firstNode, waited.NodeId);

        await new TargetTools(firstSession, firstContext).Detach();
        await using var secondSession = new ToolSession(agent.PipeName);
        var secondContext = new ManagedToolContext(secondSession, allocator);
        var secondOfficial = new StructureTools(secondSession, secondContext);
        var secondNode = Assert
            .Single((await secondOfficial.Search(fixture.Button.Name!, searchTemplates: true)).Matches).Id;

        Assert.NotEqual(firstNode, secondNode);
        var stale = await Assert.ThrowsAsync<McpException>(() =>
            new FrameworkInteractionTools(secondSession, secondContext).UiClick(firstNode));
        Assert.Contains("stale_node", stale.Message);
        Assert.Equal(2, clicks);
        Assert.True((await new FrameworkInteractionTools(secondSession, secondContext).UiClick(secondNode)).Delivered);
        Assert.Equal(3, clicks);
    }

    [AvaloniaFact]
    public async Task Content_generation_change_during_call_is_rejected_and_invalidates_previous_nodes()
    {
        using var fixture = new TargetFixture();
        long generation = 0;
        await using var agent = fixture.Attach(() => Interlocked.Read(ref generation));
        await using var session = new ToolSession(agent.PipeName);
        var context = new ManagedToolContext(session);
        var official = new StructureTools(session, context);
        var previous = Assert.Single((await official.Search(fixture.Button.Name!, searchTemplates: true)).Matches).Id;

        var staleCall = await Assert.ThrowsAsync<McpException>(() => context.WithClientAsync(CancellationToken.None,
            (_, _) =>
            {
                Interlocked.Increment(ref generation);
                return Task.FromResult(true);
            }));
        Assert.Contains("stale_session", staleCall.Message);
        var current = Assert.Single((await official.Search(fixture.Button.Name!, searchTemplates: true)).Matches).Id;
        Assert.NotEqual(previous, current);
        var staleNode =
            await Assert.ThrowsAsync<McpException>(() =>
                new FrameworkInteractionTools(session, context).UiClick(previous));
        Assert.Contains("stale_node", staleNode.Message);

        await context.WithClientAsync(CancellationToken.None,
            (_, _) =>
            {
                Assert.Equal(0, context.NativeNodeId(0));
                Assert.Equal(0, context.PublicNodeId(0));
                Assert.Equal((long)uint.MaxValue, context.NativeNodeId(context.PublicNodeId(uint.MaxValue)));
                Assert.Contains("invalid_node",
                    Assert.Throws<McpException>(() => context.PublicNodeId((long)uint.MaxValue + 1)).Message);
                Assert.Contains("stale_node", Assert.Throws<McpException>(() => context.NativeNodeId(-1)).Message);
                return Task.FromResult(true);
            });
    }

    [AvaloniaFact]
    public async Task Connection_reference_change_during_call_is_rejected_even_with_matching_content_generation()
    {
        using var fixture = new TargetFixture();
        await using var firstAgent = fixture.Attach();
        await using var secondAgent = fixture.Attach();
        await using var session = new ToolSession(firstAgent.PipeName);
        var context = new ManagedToolContext(session);
        var official = new StructureTools(session, context);
        var previous = Assert.Single((await official.Search(fixture.Button.Name!, searchTemplates: true)).Matches).Id;

        var staleCall = await Assert.ThrowsAsync<McpException>(() => context.WithClientAsync(CancellationToken.None,
            async (client, token) =>
            {
                await client.ConnectAsync(secondAgent.PipeName, token);
                return true;
            }));
        Assert.Contains("stale_session", staleCall.Message);
        var current = Assert.Single((await official.Search(fixture.Button.Name!, searchTemplates: true)).Matches).Id;
        Assert.NotEqual(previous, current);
        Assert.Contains("stale_node",
            (await Assert.ThrowsAsync<McpException>(() => new PropertyStyleTools(session, context).Props(previous)))
            .Message);
    }

    private sealed class TargetFixture : IDisposable
    {
        internal TargetFixture()
        {
            Button = new Button { Name = "ManagedContextTarget", Content = "Managed target", Width = 180, Height = 60 };
            Window = new Window { Width = 360, Height = 240, Content = Button };
            Window.Show();
            Window.UpdateLayout();
            using var frame = Window.CaptureRenderedFrame();
            Assert.NotNull(frame);
        }

        internal Button Button { get; }
        private Window Window { get; }

        internal DiagnosticsAgent Attach(Func<long>? contentGeneration = null) => DiagnosticsAgent.Attach(
            Application.Current!,
            new DiagnosticsAgentOptions
            {
                PipeName = DiagnosticsProtocol.PipePrefix + "managed-context-" + Guid.NewGuid().ToString("N"),
                ApplicationName = "Managed context tests", TopLevelProvider = () => [Window],
                ContentGenerationProvider = contentGeneration
            });

        public void Dispose() => Window.Close();
    }
}
