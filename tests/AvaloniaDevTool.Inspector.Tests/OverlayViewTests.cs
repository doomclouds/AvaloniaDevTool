using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class OverlayViewTests
{
    [AvaloniaFact]
    public async Task Options_apply_to_real_overlay_and_hover_restores_selection_without_changing_layout()
    {
        var selected = new Border { Name = "OverlaySelected", Width = 80, Height = 50, Margin = new Thickness(12), Padding = new Thickness(6), Background = Brushes.Red };
        var hovered = new Border { Name = "OverlayHovered", Width = 60, Height = 40, Margin = new Thickness(8), Background = Brushes.Blue };
        var panel = new StackPanel { Children = { selected, hovered } };
        var target = new Window { Width = 300, Height = 240, Content = panel };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "overlay-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            var root = Assert.Single(vm.Roots);
            var stack = Assert.Single(root.Children, node => node.Node.TypeName == typeof(StackPanel).FullName);
            stack.IsExpanded = true;
            await WaitUntilAsync(() => stack.Children.Any(node => node.Node.Name == selected.Name));
            var first = Assert.Single(stack.Children, node => node.Node.Name == selected.Name);
            var second = Assert.Single(stack.Children, node => node.Node.Name == hovered.Name);
            vm.SelectedNode = null;
            var overlayWindow = new Window { Content = new OverlayView { DataContext = vm } };
            overlayWindow.Show();
            using var closeOverlayWindow = new CloseWindow(overlayWindow);
            var showButton = Assert.Single(overlayWindow.GetVisualDescendants().OfType<Button>(),
                button => ReferenceEquals(button.Command, vm.ShowOverlayCommand));
            Assert.False(vm.ShowOverlayCommand.CanExecute(null));
            await WaitUntilAsync(() => !showButton.IsEffectivelyEnabled);
            vm.SelectedNode = first;
            await WaitUntilAsync(() => showButton.IsEffectivelyEnabled);
            await WaitUntilAsync(() => vm.FilteredProperties.Count > 0);
            vm.OverlayMargin = true; vm.OverlayPadding = true; vm.OverlayInfo = true;
            vm.OverlayRulers = true; vm.OverlayGuidelines = true;
            vm.ShowOverlayCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentOverlaySnapshot is { IsActive: true, Options.ShowGuidelines: true });
            Assert.NotEmpty(vm.CurrentOverlaySnapshot!.Regions);
            Assert.NotEmpty(vm.CurrentOverlaySnapshot.Rulers);
            Assert.NotEmpty(vm.CurrentOverlaySnapshot.Guidelines);
            Assert.Equal(first.Id, vm.CurrentOverlaySnapshot.ObjectId);
            Assert.Equal(new Thickness(12), selected.Margin);
            Assert.Equal(new Thickness(6), selected.Padding);
            await vm.PreviewTreeHighlightAsync(second.Id);
            await vm.RefreshOverlayAsync();
            Assert.Equal(second.Id, vm.CurrentOverlaySnapshot!.ObjectId);
            Assert.Same(first, vm.SelectedNode);
            await vm.PreviewTreeHighlightAsync(null, first.Id);
            await vm.RefreshOverlayAsync();
            Assert.Equal(second.Id, vm.CurrentOverlaySnapshot!.ObjectId);
            await vm.PreviewTreeHighlightAsync(null, second.Id);
            await vm.RefreshOverlayAsync();
            Assert.Equal(first.Id, vm.CurrentOverlaySnapshot!.ObjectId);
            vm.HideOverlayCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentOverlaySnapshot is { IsActive: false });
            Assert.False(vm.HighlightEnabled);
            vm.DisconnectCommand.Execute(null);
            await WaitUntilAsync(() => !vm.IsConnected && vm.CurrentOverlaySnapshot is null);
            Assert.False(vm.ShowOverlayCommand.CanExecute(null));
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Box_model_real_pointer_regions_restore_preferences_selection_and_ignore_stale_exit()
    {
        var clicks = 0;
        var button = new Button { Content = "Application input", Width = 100, Height = 30 };
        button.Click += (_, _) => clicks++;
        var selected = new Border { Name = "BoxSelected", Width = 180, Height = 90, Margin = new Thickness(11, 12, 13, 14),
            BorderThickness = new Thickness(2, 3, 4, 5), Padding = new Thickness(6, 7, 8, 9), Child = button };
        var other = new Border { Name = "BoxOther", Width = 70, Height = 40, Margin = new Thickness(20) };
        var target = new Window { Width = 350, Height = 260, Content = new StackPanel { Children = { selected, other } } };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "box-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            var root = Assert.Single(vm.Roots);
            var stack = Assert.Single(root.Children, node => node.Node.TypeName == typeof(StackPanel).FullName);
            stack.IsExpanded = true;
            await WaitUntilAsync(() => stack.Children.Any(node => node.Node.Name == selected.Name));
            var first = Assert.Single(stack.Children, node => node.Node.Name == selected.Name);
            var second = Assert.Single(stack.Children, node => node.Node.Name == other.Name);
            vm.SelectedNode = first;
            vm.OverlayMargin = true; vm.OverlayInfo = true;
            await WaitUntilAsync(() => vm.BoxModelSnapshot?.ObjectId == first.Id);
            vm.ShowOverlayCommand.Execute(null);
            await WaitUntilAsync(() => vm.CurrentOverlaySnapshot is { Options.ShowInfo: true });
            var view = new OverlayView { DataContext = vm };
            var host = new Window { Width = 680, Height = 800, Content = view };
            host.Show();
            using var closeHost = new CloseWindow(host);
            using var frame = host.CaptureRenderedFrame();
            if (Environment.GetEnvironmentVariable("AVDT_OVERLAY_EVIDENCE_DIR") is { Length: > 0 } evidence)
            {
                Directory.CreateDirectory(evidence);
                frame!.Save(Path.Combine(evidence, "box-model-inspector.png"), new PngBitmapEncoderOptions());
            }
            var box = Assert.Single(host.GetVisualDescendants().OfType<BoxModelView>());
            Assert.Equal(new AvaloniaDevTool.Protocol.OverlayThickness(2, 3, 4, 5), box.Snapshot!.Border);
            Assert.Equal(new AvaloniaDevTool.Protocol.OverlayRect(8, 10, 160, 66), box.Snapshot.ContentLocalRect);
            await Move(new Point(16, 132), "Margin", "Left");
            await Move(new Point(box.Bounds.Width / 2, 50), "Border", "Top");
            await Move(new Point(box.Bounds.Width - 80, 132), "Padding", "Right");
            await vm.PreviewBoxModelRegionAsync(first.Id, null, leavingRegion: "Margin", leavingSide: "Left");
            Assert.Equal("Padding", vm.CurrentOverlaySnapshot!.Options.EmphasizedRegion);
            await Move(new Point(box.Bounds.Width / 2, 132), "Content", null);
            var applicationPoint = button.TranslatePoint(new Point(50, 15), target)!.Value;
            target.MouseDown(applicationPoint, MouseButton.Left);
            target.MouseUp(applicationPoint, MouseButton.Left);
            Assert.Equal(1, clicks);
            host.MouseMove(new Point(2, 2));
            await WaitUntilAsync(() => vm.CurrentOverlaySnapshot is { Options.EmphasizedRegion: null });
            Assert.True(vm.CurrentOverlaySnapshot!.Options.ShowMargin);
            Assert.True(vm.CurrentOverlaySnapshot.Options.ShowInfo);
            Assert.Equal(first.Id, vm.CurrentOverlaySnapshot.ObjectId);
            await Move(new Point(16, 132), "Margin", "Left");
            vm.SelectedNode = second;
            await WaitUntilAsync(() => vm.BoxModelSnapshot?.ObjectId == second.Id &&
                vm.CurrentOverlaySnapshot is { Options.EmphasizedRegion: null } && vm.CurrentOverlaySnapshot.ObjectId == second.Id);
            await vm.PreviewBoxModelRegionAsync(first.Id, null, leavingRegion: "Margin", leavingSide: "Left");
            Assert.Equal(second.Id, vm.CurrentOverlaySnapshot!.ObjectId);
            Assert.Equal(new Thickness(11, 12, 13, 14), selected.Margin);
            Assert.Equal(new Thickness(2, 3, 4, 5), selected.BorderThickness);
            Assert.Equal(new Thickness(6, 7, 8, 9), selected.Padding);
            vm.HighlightEnabled = false;
            await vm.RefreshOverlayAsync();
            await WaitUntilAsync(() => vm.CurrentOverlaySnapshot is { IsActive: false });
            var firstHover = vm.PreviewBoxModelRegionAsync(second.Id, "Margin", "Left");
            var latestHover = vm.PreviewBoxModelRegionAsync(second.Id, "Content");
            await Task.WhenAll(firstHover, latestHover);
            Assert.Equal("Content", vm.CurrentOverlaySnapshot!.Options.EmphasizedRegion);
            Assert.Equal(second.Id, vm.CurrentOverlaySnapshot.ObjectId);
            await vm.PreviewBoxModelRegionAsync(second.Id, null, leavingRegion: "Content");
            Assert.False(vm.CurrentOverlaySnapshot!.IsActive);
            Assert.Null(vm.CurrentOverlaySnapshot.Options.EmphasizedRegion);
            Assert.False(vm.HighlightEnabled);
            await vm.PreviewBoxModelRegionAsync(second.Id, "Margin", "Left");
            vm.SelectedNode = null;
            await WaitUntilAsync(() => vm.CurrentOverlaySnapshot is { IsActive: false, Options.EmphasizedRegion: null } && vm.BoxModelSnapshot is null);
            vm.SelectedNode = second;
            await WaitUntilAsync(() => vm.BoxModelSnapshot?.ObjectId == second.Id);
            await vm.PreviewBoxModelRegionAsync(second.Id, "Margin", "Left");
            vm.DisconnectCommand.Execute(null);
            await WaitUntilAsync(() => !vm.IsConnected && vm.BoxModelSnapshot is null);
            host.MouseMove(new Point(2, 2));
            Assert.Null(vm.CurrentOverlaySnapshot);

            async Task Move(Point local, string region, string? side)
            {
                host.MouseMove(box.TranslatePoint(local, host)!.Value);
                await WaitUntilAsync(() => vm.CurrentOverlaySnapshot?.Options.EmphasizedRegion == region &&
                    vm.CurrentOverlaySnapshot.Options.EmphasizedSide == side);
                Assert.Equal(first.Id, vm.CurrentOverlaySnapshot!.ObjectId);
                Assert.Same(first, vm.SelectedNode);
            }
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Overlay_controls_bind_preferences_and_disable_without_a_native_connection()
    {
        await using var vm = new MainWindowViewModel();
        var window = new Window { Width = 650, Height = 420, Content = new OverlayView { DataContext = vm } };
        window.Show();
        try
        {
            Assert.False(vm.ShowOverlayCommand.CanExecute(null));
            Assert.False(vm.RefreshOverlayCommand.CanExecute(null));
            vm.OverlayMargin = true; vm.OverlayRulers = true;
            Assert.True(vm.OverlayMargin); Assert.True(vm.OverlayRulers);
            Assert.Null(vm.CurrentOverlaySnapshot);
        }
        finally { window.Close(); }
    }
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
    private sealed class CloseWindow(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
