namespace AvaloniaDevTool.NativeAutomation.Worker;

/// <summary>Observes completion of one already-inserted move without generating more input.</summary>
internal static class NativeCursorArrival
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    internal delegate bool ReadPosition(out int x, out int y);

    internal enum Outcome
    {
        Arrived,
        TimedOut,
        ReadFailed
    }

    internal readonly record struct Result(Outcome State, int X, int Y, int Reads, TimeSpan Elapsed);

    internal static Result Wait(int expectedX,
        int expectedY,
        ReadPosition readPosition,
        Func<TimeSpan> elapsed,
        Action<TimeSpan> pause)
    {
        var reads = 0;
        var x = 0;
        var y = 0;
        while (true)
        {
            // A scheduled pause can resume after its requested deadline. Do not
            // accept a later cursor observation as an in-budget confirmation.
            if (reads > 0 && elapsed() >= Budget)
                return new Result(Outcome.TimedOut, x, y, reads, elapsed());

            var succeeded = readPosition(out x, out y);
            reads++;
            if (!succeeded)
                return new Result(Outcome.ReadFailed, x, y, reads, elapsed());
            if (elapsed() > Budget)
                return new Result(Outcome.TimedOut, x, y, reads, elapsed());
            if (Math.Abs((long)x - expectedX) <= 1 && Math.Abs((long)y - expectedY) <= 1)
                return new Result(Outcome.Arrived, x, y, reads, elapsed());

            var remaining = Budget - elapsed();
            if (remaining <= TimeSpan.Zero)
                return new Result(Outcome.TimedOut, x, y, reads, elapsed());
            pause(remaining < PollInterval ? remaining : PollInterval);
        }
    }
}
