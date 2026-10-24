using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using AvaloniaDevTool.Agent;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class PopupFreezeViewTests
{
    [AvaloniaFact]
    public async Task Visible_freeze_and_resume_controls_change_real_popup_behavior_and_reset_on_disconnect()
    {
        var anchor = new Button { Content = "Open", Width = 80, Height = 30,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(20) };
        var target = new Window { Title = "Popup freeze target", Content = anchor, Width = 400, Height = 300 };
        target.Show(); target.UpdateLayout();
        var popup = new Popup { PlacementTarget = anchor, IsLightDismissEnabled = true, ShouldUseOverlayLayer = true,
            Child = new Border { Width = 100, Height = 50, Background = Brushes.White } };
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "popup-freeze-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName);
            var view = new PopupFreezeView { DataContext = vm };
            var host = new Window { Content = view, Width = 800, Height = 320 }; host.Show();
            try
            {
                Assert.False(vm.FreezePopupsCommand.CanExecute(null));
                var limitations = Find<TextBlock>(view, "Inspector.PopupFreezeLimitations");
                Assert.True(limitations.IsVisible);
                Assert.Contains("ContextMenu", limitations.Text);
                Assert.Contains("ToolTip", limitations.Text);
                Assert.Contains("transient Flyout", limitations.Text);
                await vm.InitializeAsync(); await vm.RefreshPopupFreezeAsync();
                vm.SelectedPopupFreezeRoot = Assert.Single(vm.PopupFreezeRoots, root => root.Id is not null);
                popup.Open(); target.UpdateLayout();
                var freeze = Find<Button>(view, "Inspector.FreezePopups");
                Assert.True(freeze.Command!.CanExecute(null)); freeze.Command.Execute(null);
                await WaitUntilAsync(() => vm.CurrentPopupFreeze?.IsActive == true);
                Assert.False(popup.IsLightDismissEnabled);
                target.MouseDown(new Point(350, 250), MouseButton.Left); target.MouseUp(new Point(350, 250), MouseButton.Left);
                Assert.True(popup.IsOpen);
                Assert.Contains("Overlay", vm.PopupFreezeSummary);
                Assert.Contains("部分支持", vm.PopupFreezeSummary);
                Assert.Contains("限制：", vm.PopupFreezeSummary);
                Find<Button>(view, "Inspector.ResumePopups").Command!.Execute(null);
                await WaitUntilAsync(() => vm.CurrentPopupFreeze?.IsActive == false);
                Assert.True(popup.IsLightDismissEnabled);
                vm.FreezePopupsCommand.Execute(null);
                await WaitUntilAsync(() => vm.CurrentPopupFreeze?.IsActive == true);
                vm.DisconnectCommand.Execute(null);
                await WaitUntilAsync(() => !vm.IsConnected && vm.CurrentPopupFreeze is null && popup.IsLightDismissEnabled);
                Assert.Empty(vm.PopupFreezeRoots);
                Assert.False(vm.FreezePopupsCommand.CanExecute(null));
                Assert.False(vm.ResumePopupsCommand.CanExecute(null));
            }
            finally { host.Close(); }
        }
        finally { popup.Close(); target.Close(); }
    }

    private static T Find<T>(Control view, string id) where T : Control =>
        Assert.Single(view.GetLogicalDescendants().OfType<T>(), control => AutomationProperties.GetAutomationId(control) == id);
    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(15, timeout.Token);
    }
}
