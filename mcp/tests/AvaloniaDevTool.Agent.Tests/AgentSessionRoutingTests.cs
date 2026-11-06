using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class AgentSessionRoutingTests
{
    [AvaloniaFact]
    public void Parameterless_services_ignore_payload_while_typed_routes_decode_only_when_called()
    {
        using var session = CreateSession();
        var unrelatedPayload = ProtocolJson.ToElement(42);

        var info = Assert.IsType<SessionInfo>(session.Handle(Methods.SessionInfo, unrelatedPayload));
        Assert.Equal(7, info.ContentGeneration);
        Assert.Equal(7L, session.Handle(PreviewMethods.ContentGeneration, unrelatedPayload));
        Assert.False(Assert.IsType<MetricsStatusDto>(session.Handle(MetricsMethods.Status, unrelatedPayload))
            .IsRunning);
        Assert.Empty(
            Assert.IsAssignableFrom<IReadOnlyList<BreakpointInfo>>(session.Handle(BreakpointMethods.List,
                unrelatedPayload)));
        Assert.IsType<RendererDiagnosticsStatusDto>(session.Handle(RendererDiagnosticsMethods.Status,
            unrelatedPayload));

        var roots = session.Handle(Methods.TreeRoots, ProtocolJson.ToElement(new TreeRootsRequest(TreeKind.Visual)));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<TreeNodeInfo>>(roots));
        Assert.Throws<JsonException>(() => session.Handle(Methods.TreeRoots, unrelatedPayload));
        Assert.Throws<JsonException>(() => session.Handle(Methods.PropertiesGet, unrelatedPayload));
    }

    [AvaloniaFact]
    public async Task Unknown_methods_keep_exploration_fallback_and_async_calls_keep_sync_dispatch()
    {
        using var session = CreateSession();
        var unrelatedPayload = ProtocolJson.ToElement(42);

        foreach (var method in new[] { "missing.method", "TREE.ROOTS", null })
        {
            var error = Assert.Throws<RpcException>(() => session.Handle(method!, unrelatedPayload));
            Assert.Equal("method_not_found", error.Code);
        }

        var result = await session.HandleAsync(Methods.SessionInfo, unrelatedPayload, CancellationToken.None);
        Assert.Equal(7, Assert.IsType<SessionInfo>(result).ContentGeneration);
    }

    [AvaloniaFact]
    public async Task Thread_access_and_disposal_checks_run_before_dispatch_or_payload_parsing()
    {
        using var session = CreateSession();
        var unrelatedPayload = ProtocolJson.ToElement(42);
        await Task.Run(() => Assert.Throws<InvalidOperationException>(() =>
            session.Handle(Methods.TreeRoots, unrelatedPayload)));

        session.Dispose();
        Assert.Throws<ObjectDisposedException>(() => session.Handle(Methods.TreeRoots, unrelatedPayload));
        Assert.Throws<ObjectDisposedException>(() => session.Handle("missing.method", unrelatedPayload));
    }

    private static AgentSession CreateSession() => new(Application.Current!,
        new DiagnosticsAgentOptions
        {
            ApplicationName = "Routing regression target",
            TopLevelProvider = () => [],
            ContentGenerationProvider = () => 7
        });
}
