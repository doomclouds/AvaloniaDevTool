using System.Diagnostics.Metrics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using AvaloniaDevTool.Agent;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class MetricsPointerRefreshTests
{
    [AvaloniaFact]
    public async Task Refresh_between_real_pointer_press_and_release_preserves_chart_button_and_clears_the_series()
    {
        using var meter = new Meter("MetricsPointer.Tests." + Guid.NewGuid().ToString("N"));
        var histogram = meter.CreateHistogram<int>("duration");
        var target = new Window { Content = new Border() };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "metrics-pointer-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName);
            var view = new MetricsView { DataContext = vm };
            var host = new Window { Content = view, Width = 1100, Height = 900 };
            host.Show();
            try
            {
                await vm.InitializeAsync();
                vm.InspectionTabIndex = 6;
                await Until(() => vm.CurrentMetricsStatus is not null);
                vm.MeterFilter = meter.Name;
                vm.MetricsFrameIntervalMs = 60000;
                vm.MetricsNotificationIntervalMs = 50;
                vm.StartMetricsCommand.Execute(null);
                await Until(() => vm.CurrentMetricsStatus?.IsRunning == true && vm.MetricInstruments.Count == 1);
                histogram.Record(17);
                await Until(() => vm.MetricSamples.Count == 1);
                await vm.RefreshActiveInspectionAsync();
                await Until(() => vm.MetricCharts.Count == 1);
                host.UpdateLayout();
                using var frame = host.CaptureRenderedFrame();
                var row = Assert.Single(vm.MetricCharts);
                var clear = ClearButton(view);
                var command = clear.Command;
                var clicks = 0;
                clear.Click += (_, _) => clicks++;
                var point = clear.TranslatePoint(new Point(clear.Bounds.Width / 2, clear.Bounds.Height / 2), host);
                Assert.NotNull(point);
                host.MouseMove(point.Value);
                host.MouseDown(point.Value, MouseButton.Left);
                Assert.True(clear.IsPressed);
                bool sameButton;
                try
                {
                    await vm.RefreshActiveInspectionAsync();
                    host.UpdateLayout();
                    using var refreshedFrame = host.CaptureRenderedFrame();
                    sameButton = ReferenceEquals(clear, ClearButton(view));
                }
                finally { host.MouseUp(point.Value, MouseButton.Left); }
                Assert.True(clicks == 1,
                    $"Refresh must preserve the active clear click: sameButton={sameButton}, clicks={clicks}, samples={vm.MetricSamples.Count}.");
                Assert.True(sameButton);
                Assert.Same(row, Assert.Single(vm.MetricCharts));
                Assert.Same(command, ClearButton(view).Command);
                await Until(() => vm.MetricSamples.Count == 0 && vm.MetricSeries.Single().WindowCount == 0 && row.ClearCommand.CanExecute(null));
                Assert.True(vm.CurrentMetricsStatus!.IsRunning);
                Assert.Empty(Assert.Single(vm.MetricCharts).Points);
                histogram.Record(23);
                await Until(() => vm.MetricSamples.Count == 1);
                await vm.RefreshActiveInspectionAsync();
                Assert.Same(row, Assert.Single(vm.MetricCharts));
                Assert.Same(clear, ClearButton(view));
                Assert.Equal(23, Assert.Single(row.Points).Value);
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    private static Button ClearButton(Control view) => Assert.Single(view.GetLogicalDescendants().OfType<Button>(),
        button => AutomationProperties.GetAutomationId(button) == "Inspector.ClearMetricChart");

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
