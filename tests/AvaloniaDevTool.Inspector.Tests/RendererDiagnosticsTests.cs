using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Rendering;
using AvaloniaDevTool.Agent;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class RendererDiagnosticsTests
{
    [AvaloniaFact]
    public async Task Renderer_view_uses_real_window_flags_and_resets_on_disconnect()
    {
        var target = new Window { Title = "Renderer UI target", Content = new Border() };
        target.Show();
        try
        {
            target.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.RenderTimeGraph;
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            {
                PipeName = "renderer-view-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target]
            });
            await using var vm = new MainWindowViewModel(agent.PipeName);
            var view = new RendererDiagnosticsView { DataContext = vm };
            var host = new Window { Content = view, Width = 720, Height = 400 };
            host.Show();
            try
            {
                Assert.False(vm.ApplyRendererDiagnosticsCommand.CanExecute(null));
                await vm.InitializeAsync();
                await vm.RefreshRendererDiagnosticsAsync();
                Assert.Equal(2, vm.RendererWindows.Count);
                vm.SelectedRendererWindow = vm.RendererWindows.Single(window => window.Id is not null);
                var fps = Find<CheckBox>(view, "Inspector.RendererFps");
                fps.IsChecked = true;
                Assert.True(vm.RendererFps);
                Assert.False(Find<CheckBox>(view, "Inspector.RendererFollowNewWindows").IsEnabled);
                var apply = Find<Button>(view, "Inspector.ApplyRendererDiagnostics");
                Assert.True(apply.Command!.CanExecute(null));
                apply.Command.Execute(null);
                await WaitUntilAsync(() => vm.CurrentRendererDiagnostics?.IsActive == true);
                Assert.Equal(RendererDebugOverlays.RenderTimeGraph | RendererDebugOverlays.Fps,
                    target.RendererDiagnostics.DebugOverlays);
                Assert.Contains("Renderer UI target", vm.RendererDiagnosticsSummary);
                var stop = Find<Button>(view, "Inspector.StopRendererDiagnostics");
                Assert.True(stop.Command!.CanExecute(null));
                stop.Command.Execute(null);
                await WaitUntilAsync(() => vm.CurrentRendererDiagnostics?.IsActive == false);
                Assert.Equal(RendererDebugOverlays.RenderTimeGraph, target.RendererDiagnostics.DebugOverlays);
                vm.ApplyRendererDiagnosticsCommand.Execute(null);
                await WaitUntilAsync(() => vm.CurrentRendererDiagnostics?.IsActive == true);
                vm.DisconnectCommand.Execute(null);
                await WaitUntilAsync(() => !vm.IsConnected && vm.CurrentRendererDiagnostics is null);
                await WaitUntilAsync(() => target.RendererDiagnostics.DebugOverlays == RendererDebugOverlays.RenderTimeGraph);
                Assert.Empty(vm.RendererWindows);
                Assert.False(vm.RendererFps);
                Assert.False(vm.ApplyRendererDiagnosticsCommand.CanExecute(null));
                Assert.False(vm.StopRendererDiagnosticsCommand.CanExecute(null));
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    private static T Find<T>(Control view, string id) where T : Control =>
        Assert.Single(view.GetLogicalDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(15, timeout.Token);
    }
}
