using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AvaloniaDevTool.Agent;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class LayerSceneIntegrationTests
{
    [AvaloniaFact]
    public async Task Geometry_tab_fetches_snapshot_and_pointer_selection_links_table_and_tree_without_recapturing()
    {
        var target = new Window
        {
            Width = 500,
            Height = 400,
            Content = new Border
            {
                Padding = new Thickness(20),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Name = "SceneHeading", Text = "Geometry" },
                        new Button { Name = "SceneButton", Content = "Pick geometry", Width = 180, Height = 60 }
                    }
                }
            }
        };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            {
                PipeName = "scene-ui-" + Guid.NewGuid().ToString("N"),
                TopLevelProvider = () => [target]
            });
            await using var viewModel = new MainWindowViewModel(agent.PipeName);
            await viewModel.InitializeAsync();
            Assert.True(viewModel.IsConnected, viewModel.Status);
            viewModel.SelectedNode = Assert.Single(viewModel.Roots);
            await WaitUntilAsync(() => viewModel.FilteredProperties.Count > 0, viewModel);
            viewModel.InspectionTabIndex = 8;
            await WaitUntilAsync(() => viewModel.LayoutSceneSnapshot is { Layers.Count: > 1 }, viewModel);
            var snapshot = viewModel.LayoutSceneSnapshot;
            var view = new LayerSceneView { DataContext = viewModel };
            var host = new Window { Width = 900, Height = 730, Content = view };
            host.Show();
            try
            {
                var scene = Assert.IsType<LayerSceneControl>(view.FindControl<LayerSceneControl>("LayerScene"));
                await WaitUntilAsync(() => scene.Bounds.Width > 0 && scene.Bounds.Height > 0, viewModel);
                var projected = scene.GetProjectedLayers();
                Assert.NotEmpty(projected);
                var front = projected[^1];
                var center = front.Corners.Aggregate(Vector2.Zero, (sum, point) => sum + point) / front.Corners.Count;
                Assert.Equal(front.Layer.ObjectId, LayerSceneProjection.HitTest(projected, center)!.Layer.ObjectId);
                var translated = scene.TranslatePoint(new Point(center.X, center.Y), host);
                Assert.NotNull(translated);
                host.MouseMove(translated.Value);
                host.MouseDown(translated.Value, MouseButton.Left);
                host.MouseUp(translated.Value, MouseButton.Left);
                await WaitUntilAsync(() => viewModel.SelectedNode?.Id == front.Layer.ObjectId, viewModel);
                Assert.Equal(front.Layer.ObjectId, viewModel.SelectedLayoutObjectId);
                Assert.Equal(front.Layer.ObjectId, viewModel.SelectedLayoutLayer!.ObjectId);
                Assert.Same(snapshot, viewModel.LayoutSceneSnapshot);
                viewModel.DisconnectCommand.Execute(null);
                await WaitUntilAsync(() => !viewModel.IsConnected && viewModel.LayoutSceneSnapshot is null, viewModel);
                Assert.Empty(viewModel.LayoutLayers);
            }
            finally { host.Close(); }
        }
        finally { target.Close(); }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, MainWindowViewModel viewModel)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < end) await Task.Delay(15);
        Assert.True(predicate(), viewModel.Status);
    }
}

