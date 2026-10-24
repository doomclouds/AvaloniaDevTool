using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class ProtocolMonitorTests
{
    [AvaloniaFact]
    public async Task Actual_monitor_controls_record_rpc_payloads_errors_filter_stop_and_clear()
    {
        var target = new Window { Width = 300, Height = 200, Content = new Button { Content = "Target" } };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "trace-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName);
            var view = new ProtocolMonitorView { DataContext = vm };
            var host = new Window { Width = 850, Height = 650, Content = view }; host.Show();
            try
            {
                vm.ProtocolCapturePayloads = true;
                Find<Button>(view, "Inspector.ProtocolStart").Command!.Execute(null);
                await WaitUntilAsync(() => vm.IsProtocolRecording);
                await vm.InitializeAsync();
                vm.RefreshProtocolTrace();
                var hello = Assert.Single(vm.ProtocolTraceEntries, entry => entry.Method == "session.info");
                Assert.Equal("Succeeded", hello.State);
                Assert.Contains("applicationName", hello.ResponseJson);
                vm.SelectedProtocolTrace = hello;
                Assert.Single(view.GetVisualDescendants().OfType<TabControl>()).SelectedIndex = 1;
                host.UpdateLayout();
                Assert.Equal(hello.ResponseJson, Find<TextBox>(view, "Inspector.ProtocolResponse").Text);
                vm.SelectedNode = Assert.Single(vm.Roots);
                await WaitUntilAsync(() => vm.FilteredProperties.Any(row => row.Name == "Width"));
                var width = Assert.Single(vm.FilteredProperties, row => row.Name == "Width");
                width.Draft = "invalid-number"; width.ApplyCommand.Execute(null);
                await WaitUntilAsync(() => width.HasError);
                vm.RefreshProtocolTrace();
                Assert.Contains(vm.ProtocolTraceEntries, entry => entry.Method == "properties.set" && entry.State == "Failed");
                vm.ProtocolFilter = "properties.set"; vm.ProtocolErrorsOnly = true;
                var failure = Assert.Single(vm.ProtocolTraceEntries);
                Assert.NotNull(failure.ErrorCode); vm.SelectedProtocolTrace = failure;
                Assert.Contains(failure.ErrorCode, vm.ProtocolTraceDetail);
                Find<Button>(view, "Inspector.ProtocolStop").Command!.Execute(null);
                await WaitUntilAsync(() => !vm.IsProtocolRecording);
                vm.ProtocolFilter = ""; vm.ProtocolErrorsOnly = false;
                var count = vm.ProtocolTraceEntries.Count;
                vm.RefreshCommand.Execute(null);
                await WaitUntilAsync(() => vm.RefreshCommand.CanExecute(null));
                vm.RefreshProtocolTrace(); Assert.Equal(count, vm.ProtocolTraceEntries.Count);
                Find<Button>(view, "Inspector.ProtocolClear").Command!.Execute(null);
                await WaitUntilAsync(() => vm.ProtocolTraceEntries.Count == 0);
                Assert.True(vm.IsConnected);
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Default_metadata_capture_preserves_history_and_separates_reconnected_id_spaces()
    {
        var target = new Window { Content = new Border(), Width = 200, Height = 150 }; target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "trace-reconnect-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName);
            vm.StartProtocolTraceCommand.Execute(null);
            await vm.InitializeAsync(); vm.RefreshProtocolTrace();
            var first = Assert.Single(vm.ProtocolTraceEntries, entry => entry.Method == "session.info");
            Assert.Null(first.RequestJson); Assert.Null(first.ResponseJson);
            vm.DisconnectCommand.Execute(null);
            await WaitUntilAsync(() => !vm.IsConnected && vm.ConnectCommand.CanExecute(null));
            vm.ConnectCommand.Execute(null);
            await WaitUntilAsync(() => vm.IsConnected && vm.ConnectCommand.CanExecute(null) == false);
            await WaitUntilAsync(() => { vm.RefreshProtocolTrace(); return vm.ProtocolTraceEntries.Count(entry => entry.Method == "session.info") == 2; });
            var connections = vm.ProtocolTraceEntries.Where(entry => entry.Method == "session.info").ToArray();
            Assert.Equal(connections[0].RequestId, connections[1].RequestId);
            Assert.NotEqual(connections[0].ConnectionId, connections[1].ConnectionId);
            Assert.All(connections, entry => Assert.Equal("Succeeded", entry.State));
        }
        finally { target.Close(); }
    }
    private static T Find<T>(Control root, string id) where T : Control => Assert.Single(root.GetVisualDescendants().OfType<T>(),
        control => Avalonia.Automation.AutomationProperties.GetAutomationId(control) == id);
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
