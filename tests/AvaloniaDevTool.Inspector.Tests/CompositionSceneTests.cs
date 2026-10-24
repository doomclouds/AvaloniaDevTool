using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using SkiaSharp;
using Xunit;
using Vector = Avalonia.Vector;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class CompositionSceneTests
{
    [AvaloniaFact]
    public void Projective_image_mapping_preserves_four_corners_and_real_skia_pixel_regions()
    {
        Vector2[] corners = [new(22, 18), new(150, 32), new(130, 132), new(36, 108)];
        var matrix = CompositionProjection.MapPlane(40, 30, corners);
        Point[] source = [new(0, 0), new(40, 0), new(40, 30), new(0, 30)];
        for (var index = 0; index < source.Length; index++)
        {
            var result = matrix.Transform(source[index]);
            Assert.InRange(Math.Abs(result.X - corners[index].X), 0, 0.0001);
            Assert.InRange(Math.Abs(result.Y - corners[index].Y), 0, 0.0001);
        }
        var png = FourColors();
        using var image = new Bitmap(new MemoryStream(png));
        using var output = new RenderTargetBitmap(new PixelSize(180, 160));
        using (var drawing = output.CreateDrawingContext())
        using (drawing.PushTransform(matrix)) drawing.DrawImage(image, new Rect(0, 0, 40, 30));
        using var pixels = Pixels(output);
        AssertRegion(new Point(10, 7), SKColors.Red);
        AssertRegion(new Point(30, 7), SKColors.Lime);
        AssertRegion(new Point(10, 23), SKColors.Blue);
        AssertRegion(new Point(30, 23), SKColors.Yellow);
        void AssertRegion(Point local, SKColor expected)
        {
            var point = matrix.Transform(local);
            Assert.Equal(expected, pixels.GetPixel((int)point.X, (int)point.Y));
        }
    }

    [AvaloniaFact]
    public void Scene_control_draws_real_images_clips_parent_chain_and_rejects_oversized_png_headers()
    {
        var png = Convert.ToBase64String(FourColors());
        var root = Layer(1, null, 0, png) with { ClipToBounds = true, Width = 40, Height = 30, Opacity = 1 };
        var child = Layer(2, 1, 1, png) with { TransformToRoot = new(1, 0, 0, 1, 10, 0), Transform = new(1, 0, 0, 1, 10, 0) };
        using var scene = new CompositionScene(Snapshot(root, child));
        var control = new CompositionSceneControl { Scene = scene, LayerSpacing = 0, ShowBorders = false, FrontVersion = 1 };
        control.Measure(new Size(300, 240)); control.Arrange(new Rect(0, 0, 300, 240));
        using var target = new RenderTargetBitmap(new PixelSize(300, 240));
        target.Render(control);
        using var pixels = Pixels(target);
        var imagePlane = Assert.Single(control.GetProjectedLayers(), item => item.Layer.ObjectId == 2);
        var transform = CompositionProjection.MapPlane(40, 30, imagePlane.Corners);
        var inside = transform.Transform(new Point(5, 6));
        Assert.Equal(SKColors.Red, pixels.GetPixel((int)inside.X, (int)inside.Y));
        var clipped = transform.Transform(new Point(38, 6));
        Assert.Equal(new SKColor(242, 245, 251), pixels.GetPixel((int)clipped.X, (int)clipped.Y));
        var beforeSpacing = imagePlane.Corners[0];
        control.LayerSpacing = 50;
        Assert.NotEqual(beforeSpacing, Assert.Single(control.GetProjectedLayers(), item => item.Layer.ObjectId == 2).Corners[0]);
        control.LayerSpacing = 0;
        control.MinimumDepth = 1; control.MaximumDepth = 1;
        Assert.Single(control.GetProjectedLayers());
        Assert.Equal(2, control.GetProjectedLayers()[0].Layer.ObjectId);
        var invalid = root with { PixelWidth = 1, PixelHeight = 1 };
        Assert.Equal("invalid_capture", Assert.Throws<RpcException>(() => new CompositionScene(Snapshot(invalid))).Code);
        scene.Dispose();
        Assert.Empty(scene.Images);
        Assert.Empty(control.GetProjectedLayers());
    }

    [AvaloniaFact]
    public void Border_and_gradient_switches_change_only_the_scene_pixels()
    {
        var snapshot = Snapshot(Layer(1, null, 0, null));
        using var scene = new CompositionScene(snapshot);
        var control = new CompositionSceneControl { Scene = scene, LayerSpacing = 0, ShowBorders = false, FrontVersion = 1 };
        control.Measure(new Size(300, 240)); control.Arrange(new Rect(0, 0, 300, 240));
        var projected = Assert.Single(control.GetProjectedLayers());
        var center = new Point(projected.Corners.Average(point => point.X), projected.Corners.Average(point => point.Y));
        var edge = new Point((projected.Corners[0].X + projected.Corners[3].X) / 2, (projected.Corners[0].Y + projected.Corners[3].Y) / 2);
        var plainCenter = Pixel(center);
        var plainEdge = Pixel(edge);
        control.ShowBorders = true;
        Assert.NotEqual(plainEdge, Pixel(edge));
        control.ShowBorders = false; control.ShowGradients = true;
        Assert.NotEqual(plainCenter, Pixel(center));
        Assert.Same(snapshot, scene.Snapshot);
        Assert.Empty(scene.Images);
        Assert.Equal("Empty", snapshot.Layers[0].ImageStatus);
        SKColor Pixel(Point point)
        {
            using var target = new RenderTargetBitmap(new PixelSize(300, 240)); target.Render(control);
            using var pixels = Pixels(target); return pixels.GetPixel((int)point.X, (int)point.Y);
        }
    }

    [AvaloniaFact]
    public void Camera_rotation_pan_zoom_reset_and_hit_selection_are_independent()
    {
        using var scene = new CompositionScene(Snapshot(Layer(1, null, 0, null)));
        var control = new CompositionSceneControl { Scene = scene, Width = 320, Height = 240 };
        var window = new Window { Width = 320, Height = 240, Content = control };
        window.Show();
        try
        {
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var camera = control.Camera;
            window.MouseDown(new Point(120, 100), MouseButton.Left);
            window.MouseMove(new Point(145, 115));
            window.MouseUp(new Point(145, 115), MouseButton.Left);
            Assert.NotEqual(camera.YawDegrees, control.Camera.YawDegrees);
            Assert.Equal(default, control.Pan);
            var rotated = control.Camera;
            window.MouseDown(new Point(120, 100), MouseButton.Right);
            window.MouseMove(new Point(145, 115));
            window.MouseUp(new Point(145, 115), MouseButton.Right);
            Assert.NotEqual(default, control.Pan);
            Assert.Equal(rotated.YawDegrees, control.Camera.YawDegrees);
            window.MouseWheel(new Point(100, 100), new Vector(0, 1));
            Assert.True(control.Camera.Zoom > rotated.Zoom);
            control.ResetVersion++;
            Assert.Equal(new LayerCamera(), control.Camera);
            Assert.Equal(default, control.Pan);
            control.FrontVersion++;
            Assert.Equal(0, control.Camera.YawDegrees);
            var projected = Assert.Single(control.GetProjectedLayers());
            var center = new Point(projected.Corners.Average(point => point.X), projected.Corners.Average(point => point.Y));
            window.MouseDown(center, MouseButton.Left); window.MouseUp(center, MouseButton.Left);
            Assert.Equal(1, control.SelectedCaptureId);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Composition_page_captures_real_agent_images_and_disposes_them_on_disconnect()
    {
        var target = new Window { Width = 160, Height = 140, Content = new Border { Name = "CompositionTarget", Background = Brushes.Red, Width = 80, Height = 60 } };
        target.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "composition-view-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
            await using var vm = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await vm.InitializeAsync();
            vm.SelectedNode = Assert.Single(Assert.Single(vm.Roots).Children, node => node.Node.Name == "CompositionTarget");
            await PumpUntilAsync(() => vm.FilteredProperties.Count > 0);
            vm.LayerSceneMode = 1;
            vm.InspectionTabIndex = 8;
            await PumpUntilAsync(() => vm.CapturedCompositionScene?.Images.Count > 0);
            var scene = vm.CapturedCompositionScene!;
            Assert.Contains(vm.CompositionLayers, row => row.Layer.Name == "CompositionTarget" && row.Layer.ImageStatus == "Captured");
            Assert.Contains("CompositorOwnDrawing", vm.CompositionSummary);
            Assert.True(vm.CaptureCompositionCommand.CanExecute(null));
            vm.CompositionBorders = false; vm.CompositionGradients = true; vm.CompositionMinDepth = 1;
            Assert.Same(scene, vm.CapturedCompositionScene);
            Assert.Equal(Brushes.Red, ((Border)target.Content!).Background);
            ((Border)target.Content!).Background = Brushes.Green;
            vm.CaptureCompositionCommand.Execute(null);
            await PumpUntilAsync(() => vm.CapturedCompositionScene is { Images.Count: > 0 } updated && !ReferenceEquals(scene, updated));
            Assert.True(scene.IsDisposed);
            var updatedScene = vm.CapturedCompositionScene!;
            using (var pixels = Pixels(Assert.Single(updatedScene.Images).Value)) Assert.Equal(SKColors.Green, pixels.GetPixel(5, 5));
            vm.FrontCompositionCommand.Execute(null);
            Assert.Equal(1, vm.CompositionFrontVersion);
            vm.DisconnectCommand.Execute(null);
            await PumpUntilAsync(() => !vm.IsConnected && vm.CapturedCompositionScene is null);
            Assert.True(updatedScene.IsDisposed);
            Assert.Empty(vm.CompositionLayers);
        }
        finally { target.Close(); }
    }

    private static CompositionSnapshot Snapshot(params CompositionLayer[] layers) => new(1, 50, 30, layers, layers.Length, false, true, []);
    private static CompositionLayer Layer(long id, long? parent, int depth, string? png) => new(id, parent, id,
        depth, 0, "Control", "Layer" + id, 0, 40, 30, new(1, 0, 0, 1, 0, 0), new(1, 0, 0, 1, 0, 0),
        1, true, false, null, new(0, 0, 40, 30), png is null ? 0 : 40, png is null ? 0 : 30, png, png is null ? "Empty" : "Captured", false, false, false);
    private static byte[] FourColors()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(40, 30));
        using (var drawing = bitmap.CreateDrawingContext())
        {
            drawing.FillRectangle(Brushes.Red, new Rect(0, 0, 20, 15));
            drawing.FillRectangle(Brushes.Lime, new Rect(20, 0, 20, 15));
            drawing.FillRectangle(Brushes.Blue, new Rect(0, 15, 20, 15));
            drawing.FillRectangle(Brushes.Yellow, new Rect(20, 15, 20, 15));
        }
        using var bytes = new MemoryStream(); bitmap.Save(bytes, new PngBitmapEncoderOptions()); return bytes.ToArray();
    }
    private static SKBitmap Pixels(Bitmap bitmap)
    {
        using var stream = new MemoryStream(); bitmap.Save(stream, new PngBitmapEncoderOptions()); return SKBitmap.Decode(stream.ToArray());
    }
    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10, timeout.Token);
        }
    }
}
