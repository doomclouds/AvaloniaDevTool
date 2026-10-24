using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Logging;
using AvaloniaDevTool.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class LogDetailsTests
{
    [AvaloniaFact]
    public async Task Actual_activity_view_filters_structured_logger_entries_and_jumps_to_background_source()
    {
        var logOrigin = new Button { Name = "LogSourceButton", Content = "Source" };
        var target = new Window
        {
            Content = new StackPanel { Children = { new Border { Child = new StackPanel { Children = { logOrigin } } } } }
        };
        target.Show();
        using var provider = new DiagnosticsLoggerProvider();
        using var factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        try
        {
            await using var agent = CreateAgent(target, provider);
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            vm.InspectionTabIndex = 4;
            await WaitUntilAsync(() => vm.CurrentActivityStatus is not null, vm);
            var view = new ActivityView { DataContext = vm };
            var host = new Window { Width = 1100, Height = 900, Content = view };
            host.Show();
            try
            {
                var category = view.FindControl<TextBox>("LogCategoryFilter")!;
                category.Text = "UI.Orders";
                view.FindControl<TextBox>("LogMessageFilter")!.Text = "Accepted";
                view.FindControl<TextBox>("LogParameterNameFilter")!.Text = "OrderId";
                view.FindControl<TextBox>("LogParameterValueFilter")!.Text = "84";
                Assert.Equal("UI.Orders", vm.LogCategoryFilter);
                Click(host, view.FindControl<Button>("StartActivity")!);
                await WaitUntilAsync(() => vm.CurrentActivityStatus?.IsRunning == true, vm);
                var logger = factory.CreateLogger("UI.Orders");
                await Task.Run(() =>
                {
                    logger.LogWarning("Rejected {OrderId}", 84);
                    using (logger.BeginScope("Tenant {Tenant}", "North"))
                        logger.LogError(new EventId(84, "AcceptedOrder"), new Exception("Order failure", new Exception("Inner detail")),
                            "Accepted {OrderId} from {Source}; {Optional}", 84, logOrigin, null);
                });
                await WaitUntilAsync(() => vm.ActivityEntries.Count > 0, vm);
                var entry = Assert.Single(vm.ActivityEntries);
                Assert.NotNull(entry.ObjectId);
                var list = view.FindControl<ListBox>("ActivityEntries")!;
                list.SelectedItem = entry;
                await WaitUntilAsync(() => vm.SelectedNode?.Id == entry.ObjectId, vm);
                Assert.Equal("LogSourceButton", vm.SelectedNode!.Node.Name);
                Assert.Equal("Accepted {OrderId} from {Source}; {Optional}", view.FindControl<SelectableTextBlock>("LogTemplate")!.Text);
                Assert.Equal(3, view.FindControl<ItemsControl>("LogParameters")!.ItemCount);
                Assert.Equal(LogValueState.Unavailable, vm.SelectedLogParameters[1].State);
                Assert.Equal(LogValueState.Null, vm.SelectedLogParameters[2].State);
                Assert.Contains("Order failure", view.FindControl<SelectableTextBlock>("LogException")!.Text);
                Assert.Contains("Inner detail", vm.SelectedLogException);
                Assert.Contains("North", vm.SelectedLogScopes);
                vm.RefreshInspectionCommand.Execute(null);
                await WaitUntilAsync(() => vm.RefreshInspectionCommand.CanExecute(null), vm);
                Assert.Same(entry, vm.SelectedActivityEntry);
                Assert.Same(entry, list.SelectedItem);
                Assert.Equal("Accepted {OrderId} from {Source}; {Optional}", view.FindControl<SelectableTextBlock>("LogTemplate")!.Text);
                vm.SelectedNode = Assert.Single(vm.Roots);
                await WaitUntilAsync(() => vm.SelectedNode?.Id != entry.ObjectId && vm.FilteredProperties.Count > 0, vm);
                var jump = view.FindControl<Button>("JumpToLogSource")!;
                Assert.Same(vm.JumpToLogSourceCommand, jump.Command);
                Assert.True(vm.JumpToLogSourceCommand.CanExecute(null), $"Entry={vm.SelectedActivityEntry?.ObjectId}; connected={vm.IsConnected}");
                await WaitUntilAsync(() => jump.IsEffectivelyEnabled, vm);
                Assert.True(jump.IsEffectivelyEnabled);
                var clicks = 0;
                jump.Click += (_, _) => clicks++;
                Click(host, jump);
                Assert.Equal(1, clicks);
                await WaitUntilAsync(() => vm.SelectedNode?.Id == entry.ObjectId, vm);
                Assert.Equal("LogSourceButton", vm.SelectedNode!.Node.Name);
                vm.DisconnectCommand.Execute(null);
                await WaitUntilAsync(() => !vm.IsConnected && vm.SelectedLogParameters.Count == 0 && !jump.IsEffectivelyEnabled, vm);
                Assert.Empty(vm.SelectedLogParameters);
                Assert.False(jump.IsEffectivelyEnabled);
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Expired_weak_log_source_reports_expiry_from_actual_jump_button_and_keeps_details()
    {
        var target = new Window { Content = new TextBlock { Text = "Weak source" } };
        var source = new DiagnosticsLogSource();
        target.Show();
        try
        {
            await using var agent = CreateAgent(target, source);
            await using var vm = new MainWindowViewModel(agent.PipeName);
            await vm.InitializeAsync();
            vm.InspectionTabIndex = 4;
            await WaitUntilAsync(() => vm.CurrentActivityStatus is not null, vm);
            vm.StartActivityCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentActivityStatus?.IsRunning == true, vm);
            var reference = await EmitTransientAsync(source, vm);
            for (var index = 0; index < 3; index++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            Assert.False(reference.IsAlive);
            var entry = Assert.Single(vm.ActivityEntries);
            Assert.NotNull(entry.ObjectId);
            var view = new ActivityView { DataContext = vm };
            var host = new Window { Width = 1050, Height = 850, Content = view };
            host.Show();
            try
            {
                view.FindControl<ListBox>("ActivityEntries")!.SelectedItem = entry;
                await WaitUntilAsync(() => vm.Status.Contains("已失效", StringComparison.Ordinal), vm);
                var jump = view.FindControl<Button>("JumpToLogSource")!;
                await WaitUntilAsync(() => jump.IsEffectivelyEnabled, vm);
                var clicks = 0;
                jump.Click += (_, _) => clicks++;
                Activate(host, jump);
                Assert.Equal(1, clicks);
                await WaitUntilAsync(() => vm.Status.Contains("已失效", StringComparison.Ordinal), vm);
                Assert.Equal("Transient source {Count}", view.FindControl<SelectableTextBlock>("LogTemplate")!.Text);
                Assert.Equal("1", Assert.Single(vm.SelectedLogParameters).Value);
                Assert.NotEqual(entry.ObjectId, vm.SelectedNode?.Id);
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    private static DiagnosticsAgent CreateAgent(Window target, IDiagnosticsLogSource source) =>
        DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
        {
            PipeName = "log-details-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target], LogSources = [source]
        });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> EmitTransientAsync(DiagnosticsLogSource publisher, MainWindowViewModel vm)
    {
        object? origin = new object();
        var reference = new WeakReference(origin);
        publisher.Publish(new("Warning", "Weak.UI", "Transient source {Count}", [new("Count", 1)], Source: origin));
        await WaitUntilAsync(() => vm.ActivityEntries.Count == 1, vm);
        origin = null;
        return reference;
    }

    private static void Activate(Window host, Button control)
    {
        host.UpdateLayout();
        host.Activate();
        Assert.True(control.Focus());
        host.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        host.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
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

    private static async Task WaitUntilAsync(Func<bool> condition, MainWindowViewModel vm)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < end) await Task.Delay(15);
        Assert.True(condition(), vm.Status);
    }
}
