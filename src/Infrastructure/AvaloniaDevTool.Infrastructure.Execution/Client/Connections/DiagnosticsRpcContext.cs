using System.Text.Json;
using AvaloniaDevTool.Domain.Diagnostics;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

/// <summary>Shared connection and invocation mechanics; capability clients own their typed notifications.</summary>
internal sealed class DiagnosticsRpcContext
{
    private readonly List<Func<string, JsonElement, bool>> notifications = [];
    private readonly DiagnosticMutationTracker mutations = new();

    internal DiagnosticsRpcContext()
    {
        Connection.NotificationReceived += (method, payload) =>
        {
            foreach (var handler in notifications)
                if (handler(method, payload))
                    return;
        };
    }

    internal DiagnosticsConnection Connection { get; } = new();
    internal RpcPeer Peer => Connection.Peer;

    internal void Subscribe(Func<string, JsonElement, bool> handler) => notifications.Add(handler);

    internal Task<T> InvokeAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
    {
        var peer = Peer;
        var service = method switch
        {
            MetricsMethods.Start or MetricsMethods.Stop or MetricsMethods.Clear => DiagnosticCaptureService.Metrics,
            ProfilerMethods.Start or ProfilerMethods.Stop or ProfilerMethods.Clear => DiagnosticCaptureService.Profiler,
            ActivityMethods.Start or ActivityMethods.Stop or ActivityMethods.Clear => DiagnosticCaptureService.Activity,
            _ => (DiagnosticCaptureService?)null
        };
        return service is { } selected
            ? mutations.Mutate(selected, () => peer.InvokeAsync<T>(method, parameters, cancellationToken))
            : peer.InvokeAsync<T>(method, parameters, cancellationToken);
    }

    internal long GetDiagnosticMutationEpoch(DiagnosticCaptureService service) => mutations.GetEpoch(service);

    internal Task<T> RunOwnedDiagnosticMutationAsync<T>(DiagnosticCaptureService service,
        long expectedEpoch,
        Func<Task<T>> mutation)
    {
        try
        {
            return mutations.RunOwned(service, expectedEpoch, mutation);
        }
        catch (DiagnosticOwnershipException error)
        {
            throw new RpcException("capture_ownership_lost", error.Message);
        }
    }
}
