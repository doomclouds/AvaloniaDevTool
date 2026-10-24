using System.Diagnostics.Metrics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Styling;
using AvaloniaDevTool.Agent;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class AdvancedInspectorTests
{
    [AvaloniaFact]
    public void Advanced_tabs_preserve_original_property_controls_and_expose_tools()
    {
        var window = new MainWindow();
        try
        {
            Assert.NotNull(window.FindControl<ListBox>("PropertyList"));
            var tabs = Assert.IsType<TabControl>(window.FindControl<TabControl>("InspectionTabs"));
            Assert.Equal(12, tabs.Items.Count);
            Assert.Contains(tabs.Items.OfType<TabItem>(), tab => Equals(tab.Header, "资源"));
            Assert.Contains(tabs.Items.OfType<TabItem>(), tab => Equals(tab.Header, "日志事件"));
            Assert.Contains(tabs.Items.OfType<TabItem>(), tab => Equals(tab.Header, "协议"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Style_diagnostics_are_loaded_when_styles_tab_is_selected()
    {
        var control = new TextBlock { Name = "StyledTarget", Text = "Styled", Classes = { "probe" } };
        var target = new Window { Content = control };
        target.Styles.Add(new Style(selector => selector.OfType<TextBlock>().Class("probe"))
        {
            Setters = { new Setter(TextBlock.FontSizeProperty, 27d) }
        });
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var viewModel = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await viewModel.InitializeAsync();
            viewModel.SelectedNode = Assert.Single(viewModel.Roots).Children.Single(node => node.Node.Name == "StyledTarget");
            await WaitUntilAsync(() => viewModel.FilteredProperties.Count > 0, viewModel);
            Assert.Empty(viewModel.StyleFrames);
            Assert.Empty(viewModel.ResourceDictionaries);
            Assert.Empty(viewModel.FilteredAssets);
            viewModel.InspectionTabIndex = 1;
            await WaitUntilAsync(() => viewModel.StyleSummary.Contains("个值帧"), viewModel);
            Assert.NotEmpty(viewModel.StyleFrames);
            Assert.Contains(viewModel.StyleFrames.SelectMany(frame => frame.Setters), setter => setter.PropertyName == "FontSize");
            Assert.Equal(27, control.FontSize);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Resource_edit_updates_target_and_conflict_keeps_application_newer_value()
    {
        var control = new TextBlock { Name = "ResourceTarget", Text = "Resources" };
        control.Resources["ProbeCounter"] = 12;
        var target = new Window { Content = control };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var viewModel = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await viewModel.InitializeAsync();
            viewModel.SelectedNode = Assert.Single(viewModel.Roots).Children.Single(node => node.Node.Name == "ResourceTarget");
            await WaitUntilAsync(() => viewModel.FilteredProperties.Count > 0, viewModel);
            viewModel.InspectionTabIndex = 2;
            await WaitUntilAsync(() => viewModel.ResourceDictionaries.Any(dictionary => dictionary.Name.Contains("ResourceTarget")), viewModel);
            viewModel.SelectedResourceDictionary = viewModel.ResourceDictionaries.Single(dictionary => dictionary.Name.Contains("ResourceTarget"));
            await WaitUntilAsync(() => viewModel.ResourceEntries.Any(row => row.Key == "ProbeCounter"), viewModel);
            var entry = viewModel.ResourceEntries.Single(row => row.Key == "ProbeCounter");
            entry.Draft = "48";
            entry.ApplyCommand.Execute(null);
            await WaitUntilAsync(() => Equals(control.Resources["ProbeCounter"], 48) && entry.IsOverridden, viewModel);
            control.Resources["ProbeCounter"] = 64;
            entry.ResetCommand.Execute(null);
            await WaitUntilAsync(() => entry.HasError, viewModel);
            Assert.Contains("resource_conflict", entry.Error);
            Assert.Equal(64, control.Resources["ProbeCounter"]);
            Assert.False(entry.IsOverridden);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Asset_text_preview_is_loaded_and_disconnect_clears_it()
    {
        var target = new Window { Content = new TextBlock { Text = "Assets" } };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var viewModel = new MainWindowViewModel(agent.PipeName);
            await viewModel.InitializeAsync();
            viewModel.InspectionTabIndex = 3;
            await WaitUntilAsync(() => viewModel.FilteredAssets.Any(asset => asset.Uri.EndsWith("/Assets/probe.json", StringComparison.Ordinal)), viewModel);
            viewModel.SelectedAsset = viewModel.FilteredAssets.First(asset => asset.Uri.EndsWith("/Assets/probe.json", StringComparison.Ordinal));
            await WaitUntilAsync(() => viewModel.HasAssetText, viewModel);
            Assert.Contains("Inspector asset preview integration test", viewModel.AssetText);
            viewModel.DisconnectCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsConnected && viewModel.AssetText.Length == 0, viewModel);
            Assert.Null(viewModel.AssetImage);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Activity_tab_receives_log_notifications_and_can_stop_and_clear()
    {
        var target = new Window { Content = new TextBlock { Text = "Activity" } };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var viewModel = new MainWindowViewModel(agent.PipeName);
            await viewModel.InitializeAsync();
            viewModel.InspectionTabIndex = 4;
            await WaitUntilAsync(() => viewModel.CurrentActivityStatus is not null, viewModel);
            viewModel.StartActivityCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.CurrentActivityStatus?.IsRunning == true, viewModel);
            Logger.TryGet(LogEventLevel.Warning, "Inspector.Test")?.Log(null, "Inspector activity sample");
            await WaitUntilAsync(() => viewModel.ActivityEntries.Any(entry => entry.Message == "Inspector activity sample"), viewModel);
            viewModel.StopActivityCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.CurrentActivityStatus?.IsRunning == false, viewModel);
            viewModel.ClearActivityCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.ActivityEntries.Count == 0, viewModel);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Search_result_locates_unloaded_target_through_ancestor_path()
    {
        var deepTarget = new Button { Name = "DeepTarget", Content = "Find me" };
        var nested = new StackPanel { Name = "Nested", Children = { deepTarget } };
        var container = new StackPanel { Name = "Outer", Children = { nested } };
        var target = new Window { Content = container };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var viewModel = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await viewModel.InitializeAsync();
            Assert.True(Assert.Single(Assert.Single(viewModel.Roots).Children.Single(node => node.Node.Name == "Outer").Children).IsPlaceholder);
            viewModel.TreeSearchQuery = "DeepTarget";
            viewModel.SearchTreeCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.TreeSearchResults.Count == 1, viewModel);
            viewModel.SelectedSearchResult = viewModel.TreeSearchResults[0];
            await WaitUntilAsync(() => viewModel.SelectedNode?.Node.Name == "DeepTarget" && viewModel.FilteredProperties.Count > 0, viewModel);
            Assert.Equal("DeepTarget", viewModel.SelectedNode!.Node.Name);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Pseudo_class_ui_can_temporarily_toggle_and_restore_pointerover()
    {
        var control = new Button { Name = "PseudoTarget", Content = "Pseudo" };
        var target = new Window { Content = control };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var viewModel = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await viewModel.InitializeAsync();
            viewModel.SelectedNode = Assert.Single(viewModel.Roots).Children.Single(node => node.Node.Name == "PseudoTarget");
            await WaitUntilAsync(() => viewModel.FilteredProperties.Count > 0, viewModel);
            viewModel.InspectionTabIndex = 5;
            await WaitUntilAsync(() => viewModel.PseudoClasses.Any(row => row.Name == ":pointerover"), viewModel);
            var original = viewModel.PseudoClasses.Single(row => row.Name == ":pointerover").IsActive;
            viewModel.PseudoClasses.Single(row => row.Name == ":pointerover").ToggleCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.PseudoClasses.Single(row => row.Name == ":pointerover").HasOverride, viewModel);
            Assert.NotEqual(original, viewModel.PseudoClasses.Single(row => row.Name == ":pointerover").IsActive);
            viewModel.PseudoClasses.Single(row => row.Name == ":pointerover").ResetCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.PseudoClasses.Single(row => row.Name == ":pointerover").HasOverride, viewModel);
            Assert.Equal(original, viewModel.PseudoClasses.Single(row => row.Name == ":pointerover").IsActive);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Metrics_tab_shows_raw_histogram_samples_and_retained_window_statistics()
    {
        var meterName = "Inspector.Tests." + Guid.NewGuid().ToString("N");
        using var meter = new Meter(meterName);
        var histogram = meter.CreateHistogram<double>("test.duration", "ms");
        var target = new Window { Content = new TextBlock { Text = "Metrics" } };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var viewModel = new MainWindowViewModel(agent.PipeName);
            await viewModel.InitializeAsync();
            viewModel.InspectionTabIndex = 6;
            await WaitUntilAsync(() => viewModel.CurrentMetricsStatus is not null, viewModel);
            viewModel.MeterFilter = meterName;
            viewModel.InstrumentFilter = "test.duration";
            viewModel.MetricsFrameIntervalMs = 60000;
            viewModel.StartMetricsCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.CurrentMetricsStatus?.IsRunning == true, viewModel);
            histogram.Record(10, new KeyValuePair<string, object?>("phase", "test"));
            histogram.Record(30, new KeyValuePair<string, object?>("phase", "test"));
            await WaitUntilAsync(() => viewModel.MetricSamples.Count == 2, viewModel);
            await viewModel.RefreshMetricsAsync();
            await WaitUntilAsync(() => viewModel.MetricSamples.Count == 2 && viewModel.MetricSeries.Any(series => series.WindowCount == 2), viewModel);
            var series = Assert.Single(viewModel.MetricSeries);
            Assert.Equal("20", series.Mean);
            Assert.Equal("10", series.P50);
            Assert.Equal("30", series.P90);
            Assert.Equal("30", series.P95);
            Assert.Equal(new double[] { 20 }, viewModel.MetricChartValues);
            Assert.Contains(viewModel.MetricSamples, sample => sample.RawValue == "30" && sample.NumericType == "Double" && sample.Tags == "phase=test");
            viewModel.StopMetricsCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.CurrentMetricsStatus?.IsRunning == false, viewModel);
            viewModel.DisconnectCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsConnected && viewModel.MetricSamples.Count == 0, viewModel);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Breakpoint_ui_tracks_count_conditions_and_can_disable_and_remove_definition()
    {
        var text = new TextBlock { Name = "WatchedTarget", Text = "Initial" };
        var target = new Window { Content = text };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var viewModel = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await viewModel.InitializeAsync();
            viewModel.SelectedNode = Assert.Single(viewModel.Roots).Children.Single(node => node.Node.Name == "WatchedTarget");
            await WaitUntilAsync(() => viewModel.FilteredProperties.Count > 0, viewModel);
            viewModel.InspectionTabIndex = 7;
            await WaitUntilAsync(() => viewModel.BreakpointMembers.Any(row => row.Name == "Text" && row.DeclaringType == typeof(TextBlock).FullName), viewModel);
            viewModel.SelectedBreakpointMember = viewModel.BreakpointMembers.Single(row => row.Name == "Text" && row.DeclaringType == typeof(TextBlock).FullName);
            viewModel.BreakpointComparisonChoice = viewModel.BreakpointComparisons.Single(choice => choice.Value == AvaloniaDevTool.Protocol.BreakpointComparison.GreaterThanOrEqual);
            viewModel.BreakpointCompareTo = "2";
            viewModel.CreateBreakpointCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.Breakpoints.Count == 1 && viewModel.SelectedBreakpoint is not null, viewModel);
            text.Text = "First";
            text.Text = "Second";
            await WaitUntilAsync(() => viewModel.BreakpointHits.Count >= 2, viewModel);
            Assert.Contains(viewModel.BreakpointHits, hit => hit.HitCount == 1 && !hit.ConditionMet);
            Assert.Contains(viewModel.BreakpointHits, hit => hit.HitCount == 2 && hit.ConditionMet);
            viewModel.SelectedBreakpoint!.ToggleCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.Breakpoints.Single().State == "Disabled", viewModel);
            Assert.False(viewModel.BreakpointEnabled);
            viewModel.RemoveBreakpointCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.Breakpoints.Count == 0, viewModel);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task One_shot_global_event_breakpoint_hit_can_locate_source_control()
    {
        var button = new Button { Name = "EventSource", Content = "Trigger" };
        var target = new Window { Content = button };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var viewModel = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await viewModel.InitializeAsync();
            viewModel.InspectionTabIndex = 7;
            viewModel.BreakpointKindIndex = 1;
            viewModel.BreakpointSelectedObjectOnly = false;
            viewModel.BreakpointRemoveOnHit = true;
            viewModel.CreateBreakpointCommand.Execute(null);
            await WaitUntilAsync(() => viewModel.Breakpoints.Count == 1, viewModel);
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => viewModel.BreakpointHits.Count >= 1 && viewModel.Breakpoints.Count == 0, viewModel);
            viewModel.SelectedBreakpointHit = viewModel.BreakpointHits.First();
            await WaitUntilAsync(() => viewModel.SelectedNode?.Node.Name == "EventSource", viewModel);
        }
        finally { target.Close(); }
    }

    private static DiagnosticsAgent CreateAgent(Window window) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions
        {
            PipeName = "advanced-inspector-tests-" + Guid.NewGuid().ToString("N"),
            ApplicationName = "Advanced inspector integration test",
            TopLevelProvider = () => [window]
        });

    private static async Task WaitUntilAsync(Func<bool> predicate, MainWindowViewModel viewModel)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < until)
            await Task.Delay(15);
        Assert.True(predicate(), viewModel.Status + "\nSelected: " + viewModel.SelectedNode?.Id + " / " + viewModel.SelectedNode?.Node.Name +
            "\nHits: " + System.Text.Json.JsonSerializer.Serialize(viewModel.BreakpointHits) +
            "\nSelectedHit: " + System.Text.Json.JsonSerializer.Serialize(viewModel.SelectedBreakpointHit));
    }
}

