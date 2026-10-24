using System.Diagnostics.Metrics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class MetricsIntegrationTests
{
    [AvaloniaFact]
    public void Existing_and_new_instruments_are_filtered_and_tags_form_stable_series()
    {
        using var meter = NewMeter();
        var existing = meter.CreateCounter<int>("jobs.started");
        using var service = new MetricsService((_, _) => { });
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], InstrumentNames: ["jobs.*"],
            NotificationIntervalMs: 5000));
        var later = meter.CreateHistogram<double>("jobs.duration", "ms");
        var excluded = meter.CreateCounter<int>("ignored");
        existing.Add(3, new KeyValuePair<string, object?>("region", "east"), new("status", "ok"));
        existing.Add(4, new KeyValuePair<string, object?>("status", "ok"), new("region", "east"));
        later.Record(12.5);
        excluded.Add(100);
        var snapshot = service.Snapshot();
        Assert.Equal(2, snapshot.Instruments.Count);
        Assert.DoesNotContain(snapshot.Instruments, item => item.Name == "ignored");
        Assert.Equal(3, snapshot.Samples.Count);
        Assert.Equal(snapshot.Samples[0].SeriesId, snapshot.Samples[1].SeriesId);
        Assert.Equal(["region", "status"], snapshot.Samples[0].Tags.Select(tag => tag.Key));
        Assert.Equal("Delta", snapshot.Instruments.Single(item => item.Name == "jobs.started").ValueSemantics);
        Assert.Equal("Observation", snapshot.Instruments.Single(item => item.Name == "jobs.duration").ValueSemantics);
    }

    [AvaloniaFact]
    public void Every_supported_numeric_type_is_captured_with_an_exact_raw_representation()
    {
        using var meter = NewMeter();
        using var service = new MetricsService((_, _) => { });
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], NotificationIntervalMs: 5000));
        meter.CreateCounter<byte>("byte").Add(2);
        meter.CreateCounter<short>("short").Add(3);
        meter.CreateCounter<int>("int").Add(4);
        meter.CreateCounter<long>("long").Add(9007199254740993L);
        meter.CreateCounter<float>("single").Add(1.25f);
        meter.CreateCounter<double>("double").Add(1.75);
        meter.CreateCounter<decimal>("decimal").Add(12345678901234567890.123456789m);
        var snapshot = service.Snapshot();
        Assert.Equal(7, snapshot.Samples.Count);
        Assert.Equal(7, snapshot.Samples.Select(sample => sample.NumericType).Distinct().Count());
        Assert.Equal("9007199254740993", snapshot.Samples.Single(sample => sample.NumericType == "Int64").RawValue);
        Assert.Equal("12345678901234567890.123456789", snapshot.Samples.Single(sample => sample.NumericType == "Decimal").RawValue);
    }

    [AvaloniaFact]
    public void Sample_history_and_notification_backlog_have_independent_bounds()
    {
        using var meter = NewMeter();
        var counter = meter.CreateCounter<int>("count");
        using var service = new MetricsService((_, _) => { });
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], Capacity: 3, PendingCapacity: 2,
            NotificationIntervalMs: 5000));
        for (var index = 1; index <= 5; index++)
            counter.Add(index);
        var snapshot = service.Snapshot();
        Assert.Equal([3d, 4d, 5d], snapshot.Samples.Select(sample => sample.Value));
        Assert.Equal(5, snapshot.Status.ReceivedSamples);
        Assert.Equal(2, snapshot.Status.EvictedHistorySamples);
        Assert.Equal(3, snapshot.Status.DroppedPendingSamples);
        Assert.Equal(2, snapshot.Status.PendingSampleCount);
        var series = Assert.Single(snapshot.Series);
        Assert.Equal(3, series.WindowCount);
        Assert.Equal(4, series.WindowMean);
        Assert.Equal(4, series.WindowP50);
        Assert.Equal(5, series.WindowP90);
        Assert.Equal(5, series.WindowP95);
        Assert.Equal("retained_time_window", series.WindowScope);
        Assert.Equal("nearest_rank", series.PercentileAlgorithm);
    }

    [AvaloniaFact]
    public void Series_instrument_and_tag_limits_are_reported_without_unbounded_growth()
    {
        using var meter = NewMeter();
        var counter = meter.CreateCounter<int>("count");
        using var service = new MetricsService((_, _) => { });
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], MaxSeries: 1, MaxInstruments: 1,
            MaxTags: 1, NotificationIntervalMs: 5000));
        counter.Add(1, new KeyValuePair<string, object?>("region", "east"), new("unused", "value"));
        counter.Add(2, new KeyValuePair<string, object?>("region", "west"), new("unused", "value"));
        meter.CreateCounter<int>("too-many-instruments").Add(3);
        var snapshot = service.Snapshot();
        Assert.Single(snapshot.Instruments);
        Assert.Single(snapshot.Series);
        Assert.Single(snapshot.Samples);
        Assert.Single(snapshot.Samples[0].Tags);
        Assert.Equal(1, snapshot.Status.DroppedSeriesSamples);
        Assert.Equal(1, snapshot.Status.DroppedInstruments);
        Assert.Equal(2, snapshot.Status.TruncatedTagSets);
    }

    [AvaloniaFact]
    public async Task Observable_gauges_are_polled_and_notifications_are_delivered_on_ui_thread()
    {
        using var meter = NewMeter();
        var gaugeValue = 17L;
        meter.CreateObservableGauge("cache.size", () => gaugeValue);
        var notification = new TaskCompletionSource<MetricsSamplesAddedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationsOnUiThread = true;
        using var service = new MetricsService((method, payload) =>
        {
            notificationsOnUiThread &= Dispatcher.UIThread.CheckAccess();
            if (method == MetricsMethods.SampleAdded)
                notification.TrySetResult((MetricsSamplesAddedNotification)payload);
        });
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], PollIntervalMs: 100,
            NotificationIntervalMs: 50));
        var received = await notification.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(notificationsOnUiThread);
        Assert.Contains(received.Samples, sample => sample.Value == 17);
        Assert.Equal("CurrentValue", Assert.Single(received.Instruments).ValueSemantics);
        Assert.True(Assert.Single(received.Instruments).IsObservable);
        gaugeValue = 23;
        await WaitUntilAsync(() => service.Snapshot().Samples.Any(sample => sample.Value == 23));
        service.Stop();
        var stopped = service.Status();
        gaugeValue = 99;
        await Task.Delay(250);
        Assert.Equal(stopped.ReceivedSamples, service.Status().ReceivedSamples);
        Assert.False(service.Status().IsRunning);
    }

    [AvaloniaFact]
    public async Task Stop_and_dispose_silence_callbacks_and_restart_creates_a_fresh_capture()
    {
        using var meter = NewMeter();
        var counter = meter.CreateCounter<int>("count");
        var notifications = 0;
        var service = new MetricsService((_, _) => notifications++);
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], NotificationIntervalMs: 50));
        await Task.Run(() => { for (var index = 0; index < 100; index++) counter.Add(1); });
        var firstCapture = service.Status().CaptureId;
        service.Stop();
        var received = service.Status().ReceivedSamples;
        var previousNotifications = notifications;
        await Task.Run(() => { for (var index = 0; index < 100; index++) counter.Add(1); });
        await Task.Delay(100);
        Assert.Equal(received, service.Status().ReceivedSamples);
        Assert.Equal(previousNotifications, notifications);
        Assert.Equal(0, service.Status().PendingSampleCount);
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], NotificationIntervalMs: 50));
        Assert.NotEqual(firstCapture, service.Status().CaptureId);
        Assert.Empty(service.Snapshot().Samples);
        counter.Add(8);
        Assert.Equal(8, Assert.Single(service.Snapshot().Samples).Value);
        service.Dispose();
        counter.Add(9);
        await Task.Delay(100);
        Assert.Single(service.Snapshot().Samples);
        Assert.False(service.Status().IsRunning);
        Assert.Throws<ObjectDisposedException>(() => service.Start(new MetricsStartRequest(MeterNames: [meter.Name])));
    }

    [AvaloniaFact]
    public async Task Observable_errors_and_nonfinite_samples_are_reported_without_crashing_capture()
    {
        using var meter = NewMeter();
        var histogram = meter.CreateHistogram<double>("duration");
        meter.CreateObservableGauge("broken", (Func<int>)(() => throw new InvalidOperationException("Observable failed")));
        using var service = new MetricsService((_, _) => { });
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], PollIntervalMs: 100,
            NotificationIntervalMs: 5000));
        histogram.Record(double.NaN);
        histogram.Record(double.PositiveInfinity);
        histogram.Record(3);
        await WaitUntilAsync(() => service.Status().ObservableErrors > 0);
        Assert.Equal(2, service.Status().InvalidMeasurements);
        Assert.Single(service.Snapshot().Samples);
        Assert.Contains("Observable failed", service.Status().LastError);
        Assert.True(service.Status().IsRunning);
    }

    [AvaloniaFact]
    public void Invalid_start_settings_do_not_replace_a_running_capture()
    {
        using var meter = NewMeter();
        using var service = new MetricsService((_, _) => { });
        var initial = service.Start(new MetricsStartRequest(MeterNames: [meter.Name]));
        Assert.Throws<RpcException>(() => service.Start(new MetricsStartRequest(Capacity: 1025)));
        Assert.Throws<RpcException>(() => service.Start(new MetricsStartRequest(MeterNames: [""])));
        Assert.Equal(initial.CaptureId, service.Status().CaptureId);
        Assert.True(service.Status().IsRunning);
    }


    [AvaloniaFact]
    public async Task Notification_batches_are_bounded_and_observable_counters_keep_reported_semantics()
    {
        using var meter = NewMeter();
        var counter = meter.CreateUpDownCounter<long>("delta");
        meter.CreateObservableCounter("observed.total", () => 100L);
        var batches = new List<MetricsSamplesAddedNotification>();
        using var service = new MetricsService((_, payload) => batches.Add((MetricsSamplesAddedNotification)payload));
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], PollIntervalMs: 100,
            NotificationIntervalMs: 50));
        for (var index = 0; index < 130; index++)
            counter.Add(index % 2 == 0 ? 1 : -1);
        await WaitUntilAsync(() => batches.Sum(batch => batch.Samples.Count) >= 130 &&
            service.Snapshot().Instruments.Any(instrument => instrument.Kind == "ObservableCounter"));
        Assert.All(batches, batch => Assert.InRange(batch.Samples.Count, 1, 64));
        Assert.Equal("Delta", service.Snapshot().Instruments.Single(instrument => instrument.Name == "delta").ValueSemantics);
        Assert.Equal("ReportedCumulative", service.Snapshot().Instruments.Single(instrument => instrument.Name == "observed.total").ValueSemantics);
    }

    [AvaloniaFact]
    public void Worst_case_retained_text_and_dimensions_fit_a_protocol_frame()
    {
        using var meter = new Meter("MetricsFrame." + Guid.NewGuid().ToString("N") + new string('m', 270),
            new string('v', 200));
        var instruments = Enumerable.Range(0, 128).Select(index => meter.CreateCounter<double>(
            "instrument-" + index + new string('n', 270), new string('u', 300), new string('d', 600))).ToArray();
        using var service = new MetricsService((_, _) => { });
        service.Start(new MetricsStartRequest(MeterNames: ["MetricsFrame.*"], InstrumentNames: ["instrument-*"],
            Capacity: 1024, PendingCapacity: 2048, MaxSeries: 128, MaxInstruments: 128,
            MaxTags: 8, NotificationIntervalMs: 5000));
        var tags = Enumerable.Range(0, 8).Select(index => new KeyValuePair<string, object?>(
            "key-" + index + new string('k', 100), "value-" + index + new string('x', 200))).ToArray();
        for (var index = 0; index < 1024; index++)
            instruments[index % instruments.Length].Add(1e100, tags);
        var snapshot = service.Snapshot();
        Assert.Equal(1024, snapshot.Samples.Count);
        Assert.Equal(128, snapshot.Series.Count);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot, ProtocolJson.Options).Length <
            RpcPeer.MaximumFrameBytes);
    }

    [AvaloniaFact]
    public void Tag_objects_are_not_formatted_by_invoking_application_code()
    {
        using var meter = NewMeter();
        var counter = meter.CreateCounter<int>("count");
        using var service = new MetricsService((_, _) => { });
        service.Start(new MetricsStartRequest(MeterNames: [meter.Name], NotificationIntervalMs: 5000));
        counter.Add(1, new KeyValuePair<string, object?>("object", new ThrowingTag()));
        var sample = Assert.Single(service.Snapshot().Samples);
        Assert.Contains(nameof(ThrowingTag), Assert.Single(sample.Tags).Value);
    }

    private sealed class ThrowingTag
    {
        public override string ToString() => throw new InvalidOperationException("Application code must not be called.");
    }


    [AvaloniaFact]
    public async Task Named_pipe_metrics_api_publishes_samples_and_stop_retains_a_queryable_snapshot()
    {
        using var meter = NewMeter();
        var counter = meter.CreateCounter<int>("jobs");
        var window = new Avalonia.Controls.Window { Content = new Avalonia.Controls.TextBlock { Text = "Metrics host" } };
        window.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Avalonia.Application.Current!, new DiagnosticsAgentOptions
            {
                PipeName = "metrics-rpc-" + Guid.NewGuid().ToString("N"),
                TopLevelProvider = () => [window]
            });
            await using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", agent.PipeName,
                System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            await pipe.ConnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            await using var peer = new RpcPeer(pipe);
            var notified = new TaskCompletionSource<MetricsSamplesAddedNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
            peer.NotificationReceived += (method, payload) =>
            {
                if (method == MetricsMethods.SampleAdded)
                    notified.TrySetResult(ProtocolJson.Read<MetricsSamplesAddedNotification>(payload));
            };
            peer.Start();
            var started = await peer.InvokeAsync<MetricsStatusDto>(MetricsMethods.Start,
                new MetricsStartRequest(MeterNames: [meter.Name], NotificationIntervalMs: 50));
            Assert.True(started.IsRunning);
            counter.Add(9);
            Assert.Contains((await notified.Task.WaitAsync(TimeSpan.FromSeconds(5))).Samples, sample => sample.Value == 9);
            var status = await peer.InvokeAsync<MetricsStatusDto>(MetricsMethods.Status, null);
            Assert.Equal(started.CaptureId, status.CaptureId);
            Assert.Equal([meter.Name], status.MeterNames);
            var stopped = await peer.InvokeAsync<MetricsStatusDto>(MetricsMethods.Stop, null);
            Assert.False(stopped.IsRunning);
            counter.Add(15);
            var snapshot = await peer.InvokeAsync<MetricsSnapshotDto>(MetricsMethods.Snapshot, null);
            Assert.Equal(9, Assert.Single(snapshot.Samples).Value);
        }
        finally { window.Close(); }
    }

    private static Meter NewMeter() => new("AvaloniaDevTool.Tests." + Guid.NewGuid().ToString("N"), "1.0");

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(10, timeout.Token);
    }
}

