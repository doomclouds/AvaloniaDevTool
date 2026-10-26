using System.Diagnostics.Metrics;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class MetricsWindowTests
{
    [AvaloniaFact]
    public void Time_window_keeps_its_exact_boundary_and_stop_freezes_time_and_statistics()
    {
        var clock = new ManualClock();
        using var meter = NewMeter();
        var histogram = meter.CreateHistogram<int>("duration", "ms");
        using var service = new MetricsService((_, _) => { }, clock);
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], HistoryWindowMs: 1000));
        histogram.Record(10);
        clock.Advance(1);
        histogram.Record(30);
        clock.Advance(999);
        Assert.Equal(2, service.Snapshot().Samples.Count);
        clock.Advance(1);
        var snapshot = service.Snapshot();
        Assert.Equal(30, Assert.Single(snapshot.Samples).Value);
        Assert.Equal(1, snapshot.Status.ExpiredHistorySamples);
        Assert.Equal(0, snapshot.Status.EvictedHistorySamples);
        Assert.Equal(clock.Start.AddMilliseconds(1), snapshot.Status.WindowStart);
        Assert.Equal("retained_time_window", Assert.Single(snapshot.Series).WindowScope);
        var stopped = service.Stop();
        clock.Advance(100000);
        histogram.Record(99);
        var frozen = service.Snapshot();
        Assert.Equal(stopped.WindowEnd, frozen.Status.WindowEnd);
        Assert.Equal(30, Assert.Single(frozen.Samples).Value);
        Assert.Equal(1, Assert.Single(frozen.Series).WindowCount);
    }

    [AvaloniaFact]
    public void Frames_align_across_series_use_histogram_mean_and_counter_last_without_changing_raw_percentiles()
    {
        var clock = new ManualClock();
        using var meter = NewMeter();
        var histogram = meter.CreateHistogram<int>("duration", "ms");
        var counter = meter.CreateCounter<int>("count");
        var gauge = meter.CreateGauge<int>("size");
        using var service = new MetricsService((_, _) => { }, clock);
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], FrameIntervalMs: 250));
        foreach (var value in new[] { 1, 2, 3, 4, 100 })
            histogram.Record(value);
        counter.Add(2);
        counter.Add(5);
        gauge.Record(8);
        gauge.Record(11);
        clock.Advance(249);
        var first = service.Snapshot();
        var histogramId = first.Instruments.Single(item => item.Name == "duration").Id;
        var histogramSeries = first.Series.Single(item => item.InstrumentId == histogramId);
        Assert.Equal(3, histogramSeries.WindowP50);
        Assert.Equal(100, histogramSeries.WindowP90);
        Assert.Equal(100, histogramSeries.WindowP95);
        var histogramFrame = first.Frames!.Single(frame => frame.SeriesId == histogramSeries.Id);
        Assert.Equal(22, histogramFrame.Value);
        Assert.Equal(5, histogramFrame.Count);
        Assert.Equal("observation_mean", histogramFrame.Aggregation);
        var counterSeries = first.Series.Single(item =>
            item.InstrumentId == first.Instruments.Single(item => item.Name == "count").Id);
        Assert.Equal(5, first.Frames!.Single(frame => frame.SeriesId == counterSeries.Id).Value);
        Assert.Equal("Delta", first.Instruments.Single(item => item.Name == "count").ValueSemantics);
        Assert.All(first.Frames!, frame => Assert.Equal(clock.Start, frame.Start));
        Assert.Equal(11,
            first.Frames!.Single(frame => frame.SeriesId != histogramSeries.Id && frame.SeriesId != counterSeries.Id)
                .Value);
        clock.Advance(1);
        histogram.Record(50);
        var second = service.Snapshot();
        Assert.Equal(2, second.Frames!.Count(frame => frame.SeriesId == histogramSeries.Id));
        Assert.Contains(second.Frames!, frame => frame.Start == clock.Start.AddMilliseconds(250) && frame.Value == 50);
        Assert.Equal(9, first.Samples.Count);
        Assert.Equal(10, second.Samples.Count);
    }

    [AvaloniaFact]
    public void Disabling_frame_aggregation_preserves_each_raw_point_at_its_actual_timestamp()
    {
        var clock = new ManualClock();
        using var meter = NewMeter();
        var histogram = meter.CreateHistogram<int>("duration");
        using var service = new MetricsService((_, _) => { }, clock);
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], AggregateFrames: false));
        histogram.Record(5);
        clock.Advance(10);
        histogram.Record(25);
        var snapshot = service.Snapshot();
        Assert.False(snapshot.Status.AggregateFrames);
        Assert.Equal(new double?[] { 5, 25 }, snapshot.Frames!.Select(frame => frame.Value));
        Assert.Equal(snapshot.Samples.Select(sample => sample.Timestamp),
            snapshot.Frames!.Select(frame => frame.Start));
        Assert.All(snapshot.Frames!,
            frame =>
            {
                Assert.Equal(1, frame.Count);
                Assert.Equal("raw", frame.Aggregation);
            });
        Assert.Equal(15, Assert.Single(snapshot.Series).WindowMean);
    }

    [AvaloniaFact]
    public void Clearing_one_series_removes_its_history_frames_and_pending_without_restarting_the_other_series()
    {
        var clock = new ManualClock();
        using var meter = NewMeter();
        var counter = meter.CreateCounter<int>("count");
        using var service = new MetricsService((_, _) => { }, clock);
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], NotificationIntervalMs: 5000));
        counter.Add(1, new KeyValuePair<string, object?>("series", "a"));
        counter.Add(9, new KeyValuePair<string, object?>("series", "b"));
        var before = service.Snapshot();
        var clearedId = before.Samples[0].SeriesId;
        var cleared = service.Clear(new MetricsClearRequest(clearedId));
        Assert.True(cleared.IsRunning);
        Assert.Equal(before.Status.CaptureId, cleared.CaptureId);
        Assert.Equal(before.Status.LastSequence, cleared.LastSequence);
        Assert.True(cleared.Revision > before.Status.Revision);
        Assert.Equal(1, cleared.PendingSampleCount);
        var after = service.Snapshot();
        Assert.Equal(9, Assert.Single(after.Samples).Value);
        Assert.Single(after.Frames!);
        Assert.Equal(0, after.Series.Single(item => item.Id == clearedId).WindowCount);
        counter.Add(3, new KeyValuePair<string, object?>("series", "a"));
        Assert.Equal(3, service.Snapshot().Samples[^1].Sequence);
        Assert.Equal(clearedId, service.Snapshot().Samples[^1].SeriesId);
        service.Stop();
        service.Clear(new MetricsClearRequest());
        Assert.Empty(service.Snapshot().Samples);
        Assert.All(service.Snapshot().Series, item => Assert.Equal(0, item.WindowCount));
        Assert.Throws<RpcException>(() => service.Clear(new MetricsClearRequest(9999)));
    }

    [AvaloniaFact]
    public void Expiration_and_capacity_are_independent_and_invalid_window_settings_preserve_the_capture()
    {
        var clock = new ManualClock();
        using var meter = NewMeter();
        var counter = meter.CreateCounter<int>("count");
        using var service = new MetricsService((_, _) => { }, clock);
        var started = service.Start(new MetricsStartRequest(MeterNames:
            [meter.Name],
            Capacity: 2,
            HistoryWindowMs: 1000));
        counter.Add(1);
        counter.Add(2);
        counter.Add(3);
        Assert.Equal(1, service.Status().EvictedHistorySamples);
        clock.Advance(1001);
        var expired = service.Snapshot();
        Assert.Empty(expired.Samples);
        Assert.Empty(expired.Frames!);
        Assert.Equal(2, expired.Status.ExpiredHistorySamples);
        Assert.Equal(3, expired.Status.DroppedPendingSamples);
        Assert.Throws<RpcException>(() => service.Start(new MetricsStartRequest(HistoryWindowMs: 99)));
        Assert.Throws<RpcException>(() => service.Start(new MetricsStartRequest(FrameIntervalMs: 9)));
        Assert.Throws<RpcException>(() =>
            service.Start(new MetricsStartRequest(HistoryWindowMs: 100, FrameIntervalMs: 250)));
        Assert.Equal(started.CaptureId, service.Status().CaptureId);
        Assert.True(service.Status().IsRunning);
    }

    [AvaloniaFact]
    public async Task Configured_poll_and_notification_intervals_change_actual_observable_and_delivery_activity()
    {
        using var meter = NewMeter();
        var polls = 0;
        meter.CreateObservableGauge("observable", () => ++polls);
        var counter = meter.CreateCounter<int>("count");
        var notifications = new List<MetricsSamplesAddedNotification>();
        using var service =
            new MetricsService((_, payload) => notifications.Add((MetricsSamplesAddedNotification)payload));
        service.Start(new MetricsStartRequest(MeterNames:
            [meter.Name],
            PollIntervalMs: 60000,
            NotificationIntervalMs: 5000));
        counter.Add(1);
        await Task.Delay(200);
        Assert.Equal(0, polls);
        Assert.Empty(notifications);
        service.Stop();
        service.Start(
            new MetricsStartRequest(MeterNames: [meter.Name], PollIntervalMs: 100, NotificationIntervalMs: 50));
        counter.Add(2);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (polls < 2 || !notifications.Any(batch =>
                   batch.Samples.Any(sample => sample.InstrumentId != 0 && sample.Value == 2)))
            await Task.Delay(10, timeout.Token);
        Assert.True(notifications.Count >= 2);
        Assert.Contains(notifications.SelectMany(batch => batch.Instruments), item => item.Name == "observable");
    }

    private static Meter NewMeter() => new("MetricsWindow.Tests." + Guid.NewGuid().ToString("N"));

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Start { get; } = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        private long _elapsed;
        public override DateTimeOffset GetUtcNow() => Start.AddMilliseconds(_elapsed);
        public void Advance(long milliseconds) => _elapsed += milliseconds;
    }
}
