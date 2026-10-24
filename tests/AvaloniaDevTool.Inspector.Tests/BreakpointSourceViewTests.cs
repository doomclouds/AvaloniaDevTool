using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaDevTool.Agent;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class BreakpointSourceViewTests
{
    [AvaloniaFact]
    public async Task Refreshing_an_unchanged_definition_keeps_selection_and_update_command_enabled()
    {
        var source = new Button { Name = "RefreshSource" };
        var target = new Window { Content = source };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "breakpoint-refresh-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName) { CaptureLogs = false };
            await vm.InitializeAsync();
            vm.InspectionTabIndex = 7;
            vm.BreakpointKindIndex = 1;
            vm.BreakpointSelectedObjectOnly = false;
            var view = new BreakpointsView { DataContext = vm };
            var host = new Window { Width = 1100, Height = 850, Content = view };
            host.Show();
            try
            {
                Click(host, view.FindControl<Button>("CreateBreakpoint")!);
                await WaitUntilAsync(() => vm.SelectedBreakpoint is not null, vm);
                host.UpdateLayout();
                using var frame = host.CaptureRenderedFrame();
                var selected = vm.SelectedBreakpoint;
                var disabled = false;
                EventHandler observeEnabled = (_, _) => disabled |= !vm.UpdateBreakpointCommand.CanExecute(null);
                vm.UpdateBreakpointCommand.CanExecuteChanged += observeEnabled;
                try { await vm.RefreshBreakpointsAsync(); }
                finally { vm.UpdateBreakpointCommand.CanExecuteChanged -= observeEnabled; }
                Assert.Same(selected, vm.SelectedBreakpoint);
                Assert.False(disabled, "Refreshing the same definition must not disable an in-progress update click.");
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Actual_breakpoint_page_creates_independent_source_and_receiver_filters_and_keeps_receiver_on_hit_navigation()
    {
        var first = new Button { Name = "FirstSource" };
        var second = new Button { Name = "SecondSource" };
        var parent = new StackPanel { Name = "Receiver", Children = { first, second } };
        var target = new Window { Content = parent };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "breakpoint-source-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1, CaptureLogs = false };
            await vm.InitializeAsync();
            var receiver = Assert.Single(vm.Roots).Children.Single(node => node.Node.Name == "Receiver");
            receiver.IsExpanded = true;
            await WaitUntilAsync(() => receiver.Children.Any(node => node.Node.Name == "FirstSource"), vm);
            var source = receiver.Children.Single(node => node.Node.Name == "FirstSource");
            var other = receiver.Children.Single(node => node.Node.Name == "SecondSource");
            vm.SelectedNode = source;
            vm.InspectionTabIndex = 7;
            var view = new BreakpointsView { DataContext = vm };
            var host = new Window { Width = 1100, Height = 850, Content = view };
            host.Show();
            try
            {
                view.FindControl<ComboBox>("BreakpointKind")!.SelectedIndex = 1;
                Click(host, view.FindControl<Button>("UseSelectedBreakpointSource")!);
                Assert.Equal(source.Id.ToString(), vm.BreakpointSourceObjectId);
                view.FindControl<TextBox>("BreakpointReceiverId")!.Text = receiver.Id.ToString();
                Click(host, view.FindControl<Button>("CreateBreakpoint")!);
                await WaitUntilAsync(() => vm.SelectedBreakpoint is not null, vm);
                var definition = vm.SelectedBreakpoint!.Info.Definition;
                Assert.Equal(source.Id, definition.SourceObjectId);
                Assert.Equal(receiver.Id, definition.ObjectId);
                Assert.Contains($"源 #{source.Id}", vm.SelectedBreakpoint.Scope);
                Assert.Contains($"接收节点 #{receiver.Id}", vm.SelectedBreakpoint.Scope);
                parent.AddHandler(Button.ClickEvent, (_, args) => args.Handled = true, RoutingStrategies.Bubble, true);
                second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => vm.BreakpointHits.Count == 1, vm);
                Assert.Contains("handled=True", vm.BreakpointHits[0].Summary);
                vm.SelectedBreakpointHit = vm.BreakpointHits[0];
                await WaitUntilAsync(() => vm.SelectedNode?.Id == source.Id, vm);
                view.FindControl<TextBox>("BreakpointSourceId")!.Text = other.Id.ToString();
                Assert.Equal(other.Id.ToString(), vm.BreakpointSourceObjectId);
                Click(host, view.FindControl<Button>("UpdateBreakpoint")!);
                await WaitUntilAsync(() => vm.SelectedBreakpoint?.Info.Definition.SourceObjectId == other.Id, vm);
                Assert.Equal(receiver.Id, vm.SelectedBreakpoint!.Info.Definition.ObjectId);
                first.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                second.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => vm.BreakpointHits.Count == 2, vm);
                Assert.Equal(other.Id, vm.BreakpointHits[^1].ObjectId);
                Assert.Equal(2, vm.SelectedBreakpoint.HitCount);
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Actual_breakpoint_page_supports_global_source_and_rejects_invalid_source_id()
    {
        var source = new Button { Name = "GlobalSource" };
        var target = new Window { Content = source };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "breakpoint-global-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName) { CaptureLogs = false };
            await vm.InitializeAsync();
            vm.InspectionTabIndex = 7;
            var view = new BreakpointsView { DataContext = vm };
            var host = new Window { Width = 1100, Height = 850, Content = view };
            host.Show();
            try
            {
                view.FindControl<ComboBox>("BreakpointKind")!.SelectedIndex = 1;
                view.FindControl<CheckBox>("LimitBreakpointReceiver")!.IsChecked = false;
                view.FindControl<TextBox>("BreakpointSourceId")!.Text = "bad";
                Click(host, view.FindControl<Button>("CreateBreakpoint")!);
                await WaitUntilAsync(() => vm.Status.Contains("正整数", StringComparison.Ordinal), vm);
                Assert.Empty(vm.Breakpoints);
                view.FindControl<TextBox>("BreakpointSourceId")!.Text = "";
                Click(host, view.FindControl<Button>("CreateBreakpoint")!);
                await WaitUntilAsync(() => vm.Breakpoints.Count == 1, vm);
                Assert.Null(vm.SelectedBreakpoint!.Info.Definition.SourceObjectId);
                Assert.Null(vm.SelectedBreakpoint.Info.Definition.ObjectId);
                Assert.Equal("源 全局 · 接收节点 全局", vm.SelectedBreakpoint.Scope);
                source.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => vm.BreakpointHits.Count == 1, vm);
                vm.DisconnectCommand.Execute(null);
                await WaitUntilAsync(() => !vm.IsConnected && vm.Breakpoints.Count == 0, vm);
                Assert.Empty(vm.BreakpointSourceObjectId);
                Assert.False(view.FindControl<Button>("CreateBreakpoint")!.IsEffectivelyEnabled);
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    private static void Click(Window host, Control control)
    {
        host.UpdateLayout();
        using var frame = host.CaptureRenderedFrame();
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host);
        Assert.NotNull(point);
        host.MouseMove(point.Value);
        host.MouseDown(point.Value, MouseButton.Left);
        host.MouseUp(point.Value, MouseButton.Left);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, MainWindowViewModel vm)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < end) await Task.Delay(15);
        Assert.True(predicate(), vm.Status + "\nDraft source: " + vm.BreakpointSourceObjectId + "\nSelected: " +
            System.Text.Json.JsonSerializer.Serialize(vm.SelectedBreakpoint?.Info) + "\nDefinitions: " +
            System.Text.Json.JsonSerializer.Serialize(vm.Breakpoints.Select(row => row.Info)));
    }
}
