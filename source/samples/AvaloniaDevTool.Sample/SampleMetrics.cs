using System.Diagnostics.Metrics;

namespace AvaloniaDevTool.Sample;

internal static class SampleMetrics
{
    private static readonly Meter Meter = new("AvaloniaDevTool.Sample", "1.0.0");

    private static readonly Counter<long> Actions =
        Meter.CreateCounter<long>("sample.actions", "{action}", "Raw sample button action increments.");

    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>("sample.duration", "ms", "Synthetic durations for histogram inspection.");

    private static int _queueDepth = 3;

    static SampleMetrics() => Meter.CreateObservableGauge("sample.queue_depth",
        () => Volatile.Read(ref _queueDepth),
        "{item}",
        "Current synthetic queue depth.");

    public static int RecordBatch()
    {
        Actions.Add(1, new KeyValuePair<string, object?>("operation", "sample-button"));
        foreach (var duration in new double[] { 12, 18, 27, 40, 95, 15 })
            Duration.Record(duration, new KeyValuePair<string, object?>("operation", "synthetic-duration"));
        return Interlocked.Increment(ref _queueDepth);
    }
}
