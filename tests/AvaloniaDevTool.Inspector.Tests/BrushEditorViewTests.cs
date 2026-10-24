using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class BrushEditorViewTests
{
    [AvaloniaFact]
    public async Task Actual_AXAML_edits_stops_and_coordinates_as_draft_and_apply_replaces_brush()
    {
        var original = new LinearGradientBrush { GradientStops = [new GradientStop(Colors.Red, 0), new GradientStop(Colors.Blue, 1)] };
        var target = new Border { Name = "BrushTarget", Background = original };
        await using var scope = await Scope.Create(target);
        var row = scope.Row;
        var editor = new PropertyValueEditor { DataContext = row };
        var host = new Window { Content = new ScrollViewer { Content = editor }, Width = 560, Height = 800 }; host.Show();
        try
        {
            var brush = Assert.IsType<BrushEditorViewModel>(row.BrushEditor);
            Find<TextBox>(editor, "Inspector.BrushStartPoint").Text = "10%, 20%";
            var add = Find<Button>(editor, "Inspector.BrushAddStop"); add.Command!.Execute(null);
            await Until(() => brush.Stops.Count == 3);
            var color = editor.GetVisualDescendants().OfType<TextBox>().First(control => AutomationProperties.GetAutomationId(control) == "Inspector.BrushStopColor");
            color.Text = "Lime";
            var offset = editor.GetVisualDescendants().OfType<NumericUpDown>().First(control => AutomationProperties.GetAutomationId(control) == "Inspector.BrushStopOffset");
            offset.Value = 0.25m;
            Assert.Equal("10%, 20%", brush.StartPoint); Assert.Equal("Lime", brush.Stops[0].Color); Assert.Equal(0.25, brush.Stops[0].Offset);
            Assert.True(row.IsDirty); Assert.Same(original, target.Background); Assert.Equal(Colors.Red, original.GradientStops[0].Color);
            Assert.IsType<LinearGradientBrush>(Find<Border>(editor, "Inspector.BrushDraftPreview").Background);
            scope.ViewModel.SelectedProperty = row;
            scope.ViewModel.PropertySortIndex = 4;
            target.Opacity = 0.75;
            await Until(() => scope.ViewModel.FilteredProperties.Single(item => item.Name == "Opacity").CurrentValue == "0.75");
            Assert.Same(row, scope.Row); Assert.Same(brush, row.BrushEditor); Assert.Equal(3, brush.Stops.Count);
            Assert.Same(row, scope.ViewModel.SelectedProperty);
            row.ApplyCommand.Execute(null);
            await Until(() => row.CanClear);
            var applied = Assert.IsType<LinearGradientBrush>(target.Background);
            Assert.NotSame(original, applied); Assert.Equal(3, applied.GradientStops.Count);
            Assert.Equal(RelativePoint.Parse("10%, 20%"), applied.StartPoint);
            Assert.Equal(Colors.Lime, applied.GradientStops[0].Color); Assert.Equal(0.25, applied.GradientStops[0].Offset);
            row.ClearCommand.Execute(null); await Until(() => !row.CanClear);
            Assert.Same(original, target.Background);
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public async Task Brush_kind_switch_exposes_radial_and_conic_fields_and_preserves_stop_drafts()
    {
        var target = new Border { Name = "BrushTarget", Background = Brushes.Red };
        await using var scope = await Scope.Create(target);
        var row = scope.Row;
        var editor = new PropertyValueEditor { DataContext = row };
        var host = new Window { Content = editor, Width = 540, Height = 900 }; host.Show();
        try
        {
            Find<ComboBox>(editor, "Inspector.BrushKind").SelectedItem = BrushKind.Radial;
            var brush = row.BrushEditor!;
            Assert.Equal(2, brush.Stops.Count);
            Find<TextBox>(editor, "Inspector.BrushRadiusX").Text = "75%";
            Find<TextBox>(editor, "Inspector.BrushRadiusY").Text = "25%";
            Find<TextBox>(editor, "Inspector.BrushOrigin").Text = "20%, 30%";
            Assert.True(row.IsDirty); Assert.Same(Brushes.Red, target.Background);
            row.ApplyCommand.Execute(null); await Until(() => row.CanClear);
            var radial = Assert.IsType<RadialGradientBrush>(target.Background);
            Assert.Equal(RelativeScalar.Parse("75%"), radial.RadiusX); Assert.Equal(RelativeScalar.Parse("25%"), radial.RadiusY);
            Assert.Equal(RelativePoint.Parse("20%, 30%"), radial.GradientOrigin);
            Find<ComboBox>(editor, "Inspector.BrushKind").SelectedItem = BrushKind.Conic;
            Find<NumericUpDown>(editor, "Inspector.BrushAngle").Value = 123;
            Find<TextBox>(editor, "Inspector.BrushCenter").Text = "35%, 40%";
            row.ApplyCommand.Execute(null); await Until(() => target.Background is ConicGradientBrush);
            var conic = Assert.IsType<ConicGradientBrush>(target.Background);
            Assert.Equal(123, conic.Angle); Assert.Equal(RelativePoint.Parse("35%, 40%"), conic.Center);
            Assert.Equal(2, conic.GradientStops.Count);
        }
        finally { host.Close(); }
    }

    [AvaloniaFact]
    public async Task Stop_removal_keeps_two_stops_and_invalid_color_stays_in_unapplied_draft()
    {
        var target = new Border { Name = "BrushTarget", Background = new ConicGradientBrush { GradientStops = [new GradientStop(Colors.Red, 0), new GradientStop(Colors.Blue, 1)] } };
        await using var scope = await Scope.Create(target);
        var row = scope.Row; var brush = row.BrushEditor!;
        brush.Stops[0].RemoveCommand.Execute(null); Assert.Equal(2, brush.Stops.Count);
        brush.AddStopCommand.Execute(null); Assert.Equal(3, brush.Stops.Count);
        brush.Stops[1].RemoveCommand.Execute(null); Assert.Equal(2, brush.Stops.Count);
        brush.Stops[0].Color = "bad color";
        Assert.NotEmpty(brush.Validation); Assert.True(row.IsDirty);
        row.ApplyCommand.Execute(null); await Until(() => row.HasError);
        Assert.False(row.CanClear); Assert.Equal("bad color", brush.Stops[0].Color);
        Assert.Equal(Colors.Red, Assert.IsType<ConicGradientBrush>(target.Background).GradientStops[0].Color);
        brush.Stops[0].Color = "Red";
        brush.Center = "NaN,0";
        Assert.NotEmpty(brush.Validation); Assert.Same(Brushes.Transparent, brush.Preview);
    }

    private static T Find<T>(Control root, string id) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.IsEffectivelyVisible && AutomationProperties.GetAutomationId(control) == id);
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(15, timeout.Token);
    }
    private sealed class Scope(Window window, DiagnosticsAgent agent, MainWindowViewModel viewModel) : IAsyncDisposable
    {
        public MainWindowViewModel ViewModel => viewModel;
        public PropertyRowViewModel Row => viewModel.FilteredProperties.Single(row => row.Name == "Background");
        public static async Task<Scope> Create(Border target)
        {
            var window = new Window { Content = target, Width = 160, Height = 120 }; window.Show();
            var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions { PipeName = "avdt-brush-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            var viewModel = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await viewModel.InitializeAsync();
            viewModel.SelectedNode = Assert.Single(viewModel.Roots).Children.Single(node => node.Node.Name == target.Name);
            await Until(() => viewModel.FilteredProperties.Any(row => row.Name == "Background"));
            return new Scope(window, agent, viewModel);
        }
        public async ValueTask DisposeAsync() { await viewModel.DisposeAsync(); await agent.DisposeAsync(); window.Close(); }
    }
}
