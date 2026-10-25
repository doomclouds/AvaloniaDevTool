using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public enum DiagnosticCaptureService { Metrics, Profiler, Activity }

public sealed partial class DiagnosticsClient
{
    private readonly object _diagnosticMutations = new();
    private readonly long[] _diagnosticEpochs = new long[3];

    public long GetDiagnosticMutationEpoch(DiagnosticCaptureService service) =>
        Interlocked.Read(ref _diagnosticEpochs[(int)service]);

    /// <summary>Atomically checks same-client ownership and registers one mutation, without holding the gate during its response.</summary>
    public Task<T> RunOwnedDiagnosticMutationAsync<T>(DiagnosticCaptureService service, long expectedEpoch,
        Func<Task<T>> mutation)
    {
        lock (_diagnosticMutations)
        {
            if (GetDiagnosticMutationEpoch(service) != expectedEpoch)
                throw new RpcException("capture_ownership_lost", "Another diagnostic mutation changed this capture on the same client.");
            var result = mutation();
            if (GetDiagnosticMutationEpoch(service) != expectedEpoch + 1)
                throw new RpcException("capture_ownership_lost", "The owned operation must register exactly one diagnostic mutation.");
            return result;
        }
    }

    private Task<T> InvokeDiagnosticMutation<T>(string method, Func<Task<T>> invoke)
    {
        var service = method switch
        {
            MetricsMethods.Start or MetricsMethods.Stop or MetricsMethods.Clear => DiagnosticCaptureService.Metrics,
            ProfilerMethods.Start or ProfilerMethods.Stop or ProfilerMethods.Clear => DiagnosticCaptureService.Profiler,
            ActivityMethods.Start or ActivityMethods.Stop or ActivityMethods.Clear => DiagnosticCaptureService.Activity,
            _ => (DiagnosticCaptureService?)null
        };
        if (service is not { } selected) return invoke();
        lock (_diagnosticMutations)
        {
            Interlocked.Increment(ref _diagnosticEpochs[(int)selected]);
            return invoke();
        }
    }
}
