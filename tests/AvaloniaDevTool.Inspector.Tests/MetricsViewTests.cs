using System.Diagnostics.Metrics;
using System.IO.Pipes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class MetricsViewTests
{
    [AvaloniaFact]
    public async Task Actual_metric_controls_configure_capture_show_p90_synchronized_charts_and_clear_only_one_graph()
    {
        using var meter = new Meter("MetricsView.Tests." + Guid.NewGuid().ToString("N"));
        var histogram = meter.CreateHistogram<int>("duration", "ms");
        var target = new Window { Content = new Border() };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "metrics-view-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName);
            var view = new MetricsView { DataContext = vm };
            var host = new Window { Content = view, Width = 1100, Height = 900 };
            host.Show();
            try
            {
                Assert.False(Find<Button>(view, "Inspector.StartMetrics").Command!.CanExecute(null));
                await vm.InitializeAsync();
                vm.InspectionTabIndex = 6;
                await WaitUntilAsync(() => vm.CurrentMetricsStatus is not null);
                Find<TextBox>(view, "Inspector.MeterFilter").Text = meter.Name;
                Find<NumericUpDown>(view, "Inspector.MetricsPollInterval").Value = 100;
                Find<NumericUpDown>(view, "Inspector.MetricsNotificationInterval").Value = 50;
                Find<NumericUpDown>(view, "Inspector.MetricsFrameInterval").Value = 100;
                Find<NumericUpDown>(view, "Inspector.MetricsHistoryWindow").Value = 2000;
                Assert.Equal(100, vm.MetricsPollIntervalMs);
                Assert.Equal(50, vm.MetricsNotificationIntervalMs);
                Assert.Equal(100, vm.MetricsFrameIntervalMs);
                Assert.Equal(2000, vm.MetricsHistoryWindowMs);
                Find<Button>(view, "Inspector.StartMetrics").Command!.Execute(null);
                await WaitUntilAsync(() => vm.CurrentMetricsStatus?.IsRunning == true && !vm.StartMetricsCommand.CanExecute(null));
                Assert.Equal(100, vm.CurrentMetricsStatus!.PollIntervalMs);
                Assert.Equal(100, vm.CurrentMetricsStatus.FrameIntervalMs);
                Assert.Equal(2000, vm.CurrentMetricsStatus.HistoryWindowMs);
                foreach (var value in new[] { 10, 20, 30, 40, 100 })
                    histogram.Record(value, new KeyValuePair<string, object?>("group", "a"));
                histogram.Record(7, new KeyValuePair<string, object?>("group", "b"));
                await WaitUntilAsync(() => vm.MetricSeries.Count == 2 && vm.MetricCharts.Count == 2 && vm.MetricSamples.Count == 6);
                var first = vm.MetricSeries.Single(row => row.Tags == "group=a");
                Assert.Equal("100", first.P90);
                Assert.Equal("30", first.P50);
                Assert.Contains("retained_time_window", first.Scope);
                var chartA = vm.MetricCharts.Single(row => row.Id == first.Id);
                var chartB = vm.MetricCharts.Single(row => row.Id != first.Id);
                Assert.Equal(chartA.WindowStart, chartB.WindowStart);
                Assert.Equal(chartA.WindowEnd, chartB.WindowEnd);
                Assert.Equal(2000, (chartA.WindowEnd - chartA.WindowStart).TotalMilliseconds);
                await WaitUntilAsync(() => view.GetLogicalDescendants().OfType<MetricLineChart>().Count() == 2);
                var controls = view.GetLogicalDescendants().OfType<MetricLineChart>().ToArray();
                Assert.Equal(controls[0].WindowStart, controls[1].WindowStart);
                Assert.Equal(controls[0].WindowEnd, controls[1].WindowEnd);
                Assert.Contains(view.GetLogicalDescendants().OfType<TextBlock>(), block => block.Text == "p90 100");
                var clear = view.GetLogicalDescendants().OfType<Button>().Single(button =>
                    AutomationProperties.GetAutomationId(button) == "Inspector.ClearMetricChart" &&
                    button.DataContext is MetricChartRowViewModel row && row.Id == first.Id);
                clear.Command!.Execute(null);
                await WaitUntilAsync(() => vm.MetricSeries.Single(row => row.Id == first.Id).WindowCount == 0 && vm.MetricSamples.Count == 1);
                Assert.Equal(7, Assert.Single(vm.MetricSamples).Value);
                Assert.Empty(vm.MetricCharts.Single(row => row.Id == first.Id).Points);
                Assert.Single(vm.MetricCharts.Single(row => row.Id != first.Id).Points);
                Find<Button>(view, "Inspector.StopMetrics").Command!.Execute(null);
                await WaitUntilAsync(() => vm.CurrentMetricsStatus?.IsRunning == false);
                var frozen = vm.CurrentMetricsStatus!.WindowEnd;
                await Task.Delay(220);
                await vm.RefreshMetricsAsync();
                Assert.Equal(frozen, vm.CurrentMetricsStatus.WindowEnd);
                vm.DisconnectCommand.Execute(null);
                await WaitUntilAsync(() => !vm.IsConnected && vm.MetricCharts.Count == 0);
                Assert.False(vm.ClearMetricSeriesCommand.CanExecute(null));
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Display_interval_delays_actual_chart_refresh_and_raw_toggle_survives_the_start_command()
    {
        using var meter = new Meter("MetricsDisplay.Tests." + Guid.NewGuid().ToString("N"));
        var histogram = meter.CreateHistogram<int>("duration");
        var target = new Window { Content = new Border() };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "metrics-display-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName);
            var view = new MetricsView { DataContext = vm };
            var host = new Window { Content = view, Width = 1050, Height = 800 };
            host.Show();
            try
            {
                await vm.InitializeAsync();
                vm.InspectionTabIndex = 6;
                await WaitUntilAsync(() => vm.CurrentMetricsStatus is not null);
                vm.MeterFilter = meter.Name;
                Find<NumericUpDown>(view, "Inspector.MetricsFrameInterval").Value = 5000;
                Find<CheckBox>(view, "Inspector.MetricsAggregateFrames").IsChecked = false;
                vm.MetricsNotificationIntervalMs = 50;
                vm.StartMetricsCommand.Execute(null);
                await WaitUntilAsync(() => vm.CurrentMetricsStatus?.IsRunning == true && vm.StopMetricsCommand.CanExecute(null));
                await WaitUntilAsync(() => vm.MetricInstruments.Count == 1);
                await vm.RefreshMetricsAsync();
                histogram.Record(10);
                histogram.Record(30);
                await WaitUntilAsync(() => vm.MetricSamples.Count == 2);
                await Task.Delay(150);
                Assert.Empty(vm.MetricCharts);
                Assert.False(vm.CurrentMetricsStatus!.AggregateFrames);
                Assert.Equal(5000, vm.CurrentMetricsStatus.FrameIntervalMs);
                await vm.RefreshMetricsAsync();
                Assert.Equal(new double[] { 10, 30 }, Assert.Single(vm.MetricCharts).Points.Select(point => point.Value));
                vm.StopMetricsCommand.Execute(null);
                await WaitUntilAsync(() => vm.CurrentMetricsStatus?.IsRunning == false && vm.StartMetricsCommand.CanExecute(null));
                Find<NumericUpDown>(view, "Inspector.MetricsFrameInterval").Value = 50;
                vm.StartMetricsCommand.Execute(null);
                await WaitUntilAsync(() => vm.CurrentMetricsStatus?.IsRunning == true && vm.StopMetricsCommand.CanExecute(null));
                histogram.Record(99);
                await WaitUntilAsync(() => vm.MetricCharts.Count == 1 && vm.MetricCharts[0].Points.Count == 1);
                Assert.Equal(99, vm.MetricCharts[0].Points[0].Value);
                Assert.Equal(50, vm.CurrentMetricsStatus!.FrameIntervalMs);
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Older_pull_cannot_replace_new_notification_or_clear_and_foreign_capture_cannot_resurrect_old_data()
    {
        var name = "metrics-race-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var instrument = new MetricInstrumentDto(1, "Race", null, "duration", null, "ms", "Histogram", false, "Observation", true);
        var timestamp = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        var a = new MetricSampleDto(1, 1, 1, timestamp, 10, "Int32", "10", []);
        var b = a with { Sequence = 2, Value = 30, RawValue = "30" };
        var status = NewStatus("first", 1, timestamp);
        MetricSampleDto[] samples = [a];
        var shouldBlock = false;
        await using var peer = new RpcPeer(pipe, async (method, _, token) =>
        {
            switch (method)
            {
                case Methods.SessionInfo: return new SessionInfo("metrics-race", "Race", 1, "12.1.4", DiagnosticsProtocol.Version);
                case Methods.TreeRoots: return new[] { new TreeNodeInfo(1, "Window", "Root", null, false) };
                case Methods.TreeChildren: return Array.Empty<TreeNodeInfo>();
                case Methods.Highlight: return new OperationResult();
                case MetricsMethods.Snapshot:
                    var capturedStatus = status;
                    var capturedSamples = samples;
                    if (shouldBlock)
                    {
                        shouldBlock = false;
                        entered.TrySetResult();
                        await release.Task.WaitAsync(token);
                    }
                    return new MetricsSnapshotDto(capturedStatus, [instrument],
                        [new MetricSeriesDto(1, 1, [], capturedSamples.Length, null, null, null, null, null, null, null)], capturedSamples, []);
                case MetricsMethods.Clear:
                    status = status with { Revision = status.Revision + 1, BufferedSampleCount = 0 };
                    samples = [];
                    return status;
                case MetricsMethods.Stop: return status = status with { IsRunning = false, Revision = status.Revision + 1 };
                case MetricsMethods.Start:
                    samples = [];
                    return status = NewStatus("second", 0, timestamp);
                default: throw new RpcException("unsupported", method);
            }
        });
        var connection = pipe.WaitForConnectionAsync();
        await using var vm = new MainWindowViewModel(name);
        var initialize = vm.InitializeAsync();
        await connection;
        peer.Start();
        await initialize;
        try
        {
            await vm.RefreshMetricsAsync();
            shouldBlock = true;
            var pull = vm.RefreshMetricsAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            status = status with { Revision = 2, LastSequence = 2, BufferedSampleCount = 2 };
            samples = [a, b];
            await peer.SendNotificationAsync(MetricsMethods.SampleAdded, new MetricsSamplesAddedNotification([b], [instrument], status));
            await WaitUntilAsync(() => vm.MetricSamples.Count == 2);
            release.TrySetResult();
            await pull;
            Assert.Equal(2, vm.CurrentMetricsStatus!.Revision);
            Assert.Equal(new double[] { 10, 30 }, vm.MetricSamples.Select(row => row.Value));
            await vm.RefreshMetricsAsync();
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            shouldBlock = true;
            pull = vm.RefreshMetricsAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.ClearMetricSeriesCommand.Execute(null);
            release.TrySetResult();
            await pull;
            await WaitUntilAsync(() => vm.CurrentMetricsStatus?.Revision == 3 && vm.MetricSamples.Count == 0 && vm.ClearMetricSeriesCommand.CanExecute(null));
            await peer.SendNotificationAsync(MetricsMethods.SampleAdded,
                new MetricsSamplesAddedNotification([a, b], [instrument], NewStatus("first", 2, timestamp)));
            await Task.Delay(50);
            Assert.Empty(vm.MetricSamples);
            vm.StopMetricsCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentMetricsStatus?.IsRunning == false && vm.StartMetricsCommand.CanExecute(null));
            vm.StartMetricsCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentMetricsStatus?.CaptureId == "second" && vm.StopMetricsCommand.CanExecute(null));
            await peer.SendNotificationAsync(MetricsMethods.SampleAdded,
                new MetricsSamplesAddedNotification([a, b], [instrument], NewStatus("first", 999, timestamp)));
            await Task.Delay(50);
            Assert.Equal("second", vm.CurrentMetricsStatus!.CaptureId);
            Assert.Empty(vm.MetricSamples);
        }
        finally { release.TrySetResult(); }
    }

    private static MetricsStatusDto NewStatus(string capture, long revision, DateTimeOffset timestamp) => new(
        IsRunning: true, CaptureId: capture, Capacity: 512, PendingCapacity: 256, MaxSeries: 64,
        MaxInstruments: 128, MaxTags: 8, PollIntervalMs: 1000, NotificationIntervalMs: 200,
        InstrumentCount: 1, SeriesCount: 1, BufferedSampleCount: 1, PendingSampleCount: 0,
        ReceivedSamples: revision, DroppedPendingSamples: 0, EvictedHistorySamples: 0,
        DroppedSeriesSamples: 0, DroppedInstruments: 0, TruncatedTagSets: 0, InvalidMeasurements: 0,
        ObservableErrors: 0, NotificationErrors: 0, LastError: null, WindowStart: timestamp.AddSeconds(-60),
        WindowEnd: timestamp, Revision: revision, LastSequence: revision);

    private static T Find<T>(Control view, string id) where T : Control =>
        Assert.Single(view.GetLogicalDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
