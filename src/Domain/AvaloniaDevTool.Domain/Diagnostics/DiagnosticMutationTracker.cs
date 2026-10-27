using AvaloniaDevTool.Client;

namespace AvaloniaDevTool.Domain.Diagnostics;

/// <summary>Tracks ownership changes on one diagnostic connection, including direct source operations.</summary>
public sealed class DiagnosticMutationTracker
{
    private readonly object sync = new();
    private readonly long[] epochs = new long[3];

    public long GetEpoch(DiagnosticCaptureService service) => Interlocked.Read(ref epochs[(int)service]);

    public T Mutate<T>(DiagnosticCaptureService service, Func<T> mutation)
    {
        lock (sync)
        {
            Interlocked.Increment(ref epochs[(int)service]);
            return mutation();
        }
    }

    public T RunOwned<T>(DiagnosticCaptureService service, long expectedEpoch, Func<T> mutation)
    {
        lock (sync)
        {
            if (GetEpoch(service) != expectedEpoch)
                throw new DiagnosticOwnershipException(
                    "Another diagnostic mutation changed this capture on the same client.");
            var result = mutation();
            if (GetEpoch(service) != expectedEpoch + 1)
                throw new DiagnosticOwnershipException(
                    "The owned operation must register exactly one diagnostic mutation.");
            return result;
        }
    }
}

public sealed class DiagnosticOwnershipException(string message) : InvalidOperationException(message);
