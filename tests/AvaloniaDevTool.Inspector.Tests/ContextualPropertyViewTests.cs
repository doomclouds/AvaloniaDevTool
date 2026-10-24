using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class ContextualPropertyViewTests
{
    [AvaloniaFact]
    public async Task Actual_context_checkbox_preserves_hidden_drafts_and_tracks_reparenting()
    {
        var target = new Border { Name = "ContextTarget", Width = 120 };
        var grid = new Grid { Name = "ContextGrid", Children = { target } }; var canvas = new Canvas();
        Canvas.SetLeft(target, 20);
        var window = new Window { Content = new StackPanel { Children = { grid, canvas } } }; window.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions { PipeName = "context-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            var stack = Assert.Single(vm.Roots).Children.Single(node => node.TypeName == "StackPanel"); stack.IsExpanded = true;
            await Until(() => stack.Children.Any(node => node.Node.Name == "ContextGrid"));
            var gridNode = stack.Children.Single(node => node.Node.Name == "ContextGrid"); gridNode.IsExpanded = true;
            await Until(() => gridNode.Children.Any(node => node.Node.Name == "ContextTarget"));
            vm.SelectedNode = gridNode.Children.Single(node => node.Node.Name == "ContextTarget");
            await Until(() => vm.FilteredProperties.Any(row => row.Id == "Avalonia.Controls.Grid:Row"));
            Assert.DoesNotContain(vm.FilteredProperties, row => row.Id == "Avalonia.Controls.Canvas:Left");
            var host = new MainWindow { DataContext = vm }; host.Show();
            try
            {
                var checkbox = Assert.Single(host.GetVisualDescendants().OfType<CheckBox>(), control => AutomationProperties.GetAutomationId(control) == "Inspector.ContextualProperties");
                Assert.True(checkbox.IsChecked);
                Click(host, checkbox);
                await Until(() => vm.FilteredProperties.Any(row => row.Id == "Avalonia.Controls.Canvas:Left"));
                var left = vm.FilteredProperties.Single(row => row.Id == "Avalonia.Controls.Canvas:Left");
                Assert.True(left.HasContextNotice); left.Draft = "37";
                Click(host, checkbox);
                Assert.DoesNotContain(vm.FilteredProperties, row => row.Id == left.Id);
                Click(host, checkbox);
                Assert.Same(left, vm.FilteredProperties.Single(row => row.Id == left.Id)); Assert.Equal("37", left.Draft);
                Click(host, checkbox);
                var previousSelection = vm.SelectedNode;
                grid.Children.Remove(target); canvas.Children.Add(target);
                vm.RefreshCommand.Execute(null);
                await Until(() => vm.SelectedNode is { } selected && !ReferenceEquals(selected, previousSelection));
                await Until(() => vm.FilteredProperties.Any(row => row.Id == left.Id));
                Assert.DoesNotContain(vm.FilteredProperties, row => row.Id == "Avalonia.Controls.Grid:Row");
                var moved = vm.FilteredProperties.Single(row => row.Id == left.Id);
                Assert.Same(left, moved); Assert.Equal("37", moved.Draft); Assert.False(moved.HasContextNotice);
                Assert.Equal(20, Canvas.GetLeft(target));
            }
            finally { host.Close(); }
        }
        finally { window.Close(); }
    }
    private static void Click(Window host, Control control)
    {
        host.UpdateLayout(); using var frame = host.CaptureRenderedFrame();
        var origin = control.TranslatePoint(default, host)!.Value;
        var point = origin + new Point(control.Bounds.Width / 2, control.Bounds.Height / 2);
        host.MouseDown(point, MouseButton.Left); host.MouseUp(point, MouseButton.Left);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
