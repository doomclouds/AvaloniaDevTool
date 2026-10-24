using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using SkiaSharp;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class CompositionInspectionTests
{
    [AvaloniaFact]
    public async Task Own_drawing_images_do_not_repeat_child_paints_or_ancestor_backgrounds()
    {
        var child = new Border { Name = "Red", Width = 30, Height = 20, Background = Brushes.Red };
        var root = new Canvas { Width = 120, Height = 90, Background = Brushes.Blue, Children = { child } };
        Canvas.SetLeft(child, 20);
        Canvas.SetTop(child, 10);
        using var fixture = new Fixture(root);
        var snapshot = await fixture.Capture();

        Assert.Equal("CompositorOwnDrawing", snapshot.CaptureKind);
        Assert.True(snapshot.SupportsExactComposite, string.Join("; ", snapshot.Limitations));
        Assert.Equal(2, snapshot.Layers.Count);
        var rootLayer = snapshot.Layers.Single(layer => layer.ObjectId == fixture.RootId);
        var childLayer = snapshot.Layers.Single(layer => layer.Name == "Red");
        using var rootImage = Decode(rootLayer);
        using var childImage = Decode(childLayer);
        Assert.Equal(SKColors.Blue, rootImage.GetPixel(25, 15));
        Assert.Equal(SKColors.Red, childImage.GetPixel(5, 5));
        Assert.Equal(rootLayer.CaptureId, childLayer.ParentCaptureId);
        Assert.Equal(20, childLayer.Transform.OffsetX);
        Assert.Equal(10, childLayer.Transform.OffsetY);
        Assert.Equal(30, childLayer.PixelWidth);
        Assert.Equal(20, childLayer.PixelHeight);
        Assert.Equal(1, childLayer.Opacity);
        Assert.Equal(SKColors.Blue, rootImage.GetPixel(119, 89));
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(0.5)]
    public async Task Hierarchical_transform_opacity_clip_and_z_order_recompose_to_normal_screenshot_pixels(double opacity)
    {
        var back = new Border { Name = "Back", Width = 25, Height = 20, Background = Brushes.Red };
        var front = new Border
        {
            Name = "Front", Width = 20, Height = 10, Background = Brushes.Lime, Opacity = opacity,
            RenderTransformOrigin = RelativePoint.TopLeft,
            RenderTransform = new ScaleTransform(2, 2), ZIndex = 5
        };
        var group = new Canvas
        {
            Name = "Group", Width = 40, Height = 30, Background = Brushes.Yellow,
            ClipToBounds = true, Clip = new RectangleGeometry(new Rect(0, 0, 35, 25)),
            Opacity = opacity, Children = { front, back }
        };
        Canvas.SetLeft(group, 10);
        Canvas.SetTop(group, 15);
        Canvas.SetLeft(back, 5);
        Canvas.SetTop(back, 5);
        Canvas.SetLeft(front, 15);
        Canvas.SetTop(front, 10);
        var root = new Canvas { Width = 100, Height = 80, Background = Brushes.Blue, Children = { group } };
        using var fixture = new Fixture(root);
        var snapshot = await fixture.Capture();
        var children = snapshot.Layers.Where(layer => layer.ParentCaptureId == snapshot.Layers.Single(layer => layer.Name == "Group").CaptureId).ToArray();
        Assert.Equal(new[] { "Back", "Front" }, children.Select(layer => layer.Name!).ToArray());
        Assert.Equal(5, children[1].ZIndex);
        Assert.Equal(2, children[1].Transform.M11);
        Assert.Equal(25, children[1].TransformToRoot.OffsetX);
        Assert.Equal(25, children[1].TransformToRoot.OffsetY);
        Assert.Equal(opacity, children[1].Opacity);
        Assert.True(snapshot.Layers.Single(layer => layer.Name == "Group").ClipToBounds);

        using var expected = fixture.Window.CaptureRenderedFrame()!;
        var reconstructed = new CompositeControl(snapshot) { Width = 100, Height = 80 };
        reconstructed.Measure(new Size(100, 80));
        reconstructed.Arrange(new Rect(0, 0, 100, 80));
        using var actual = new RenderTargetBitmap(new PixelSize(100, 80));
        actual.Render(reconstructed);
        using var expectedPixels = Decode(expected);
        using var actualPixels = Decode(actual);
        for (var y = 0; y < 80; y++)
            for (var x = 0; x < 100; x++)
            {
                var expectedPixel = expectedPixels.GetPixel(x, y);
                var actualPixel = actualPixels.GetPixel(x, y);
                if (opacity == 1) Assert.Equal(expectedPixel, actualPixel);
                else
                {
                    // Two fractional opacity scopes each introduce at most one 8-bit rounding unit.
                    Assert.InRange(Math.Abs(expectedPixel.Red - actualPixel.Red), 0, 2);
                    Assert.InRange(Math.Abs(expectedPixel.Green - actualPixel.Green), 0, 2);
                    Assert.InRange(Math.Abs(expectedPixel.Blue - actualPixel.Blue), 0, 2);
                    Assert.Equal(expectedPixel.Alpha, actualPixel.Alpha);
                    Assert.False(snapshot.SupportsExactComposite);
                }
            }
        reconstructed.Dispose();
    }

    [AvaloniaFact]
    public async Task Overflow_bounds_transparency_and_unmapped_compositor_nodes_are_real_content()
    {
        var root = new OverflowControl { Width = 60, Height = 40 };
        using var fixture = new Fixture(root);
        var compositor = ElementComposition.GetElementVisual(root)!.Compositor;
        var custom = compositor.CreateCustomVisual(new CustomPaint());
        custom.Size = new Vector(12, 8);
        custom.Offset = new System.Numerics.Vector3(25, 12, 0);
        ElementComposition.SetElementChildVisual(root, custom);
        var snapshot = await fixture.Capture();
        var owner = snapshot.Layers.Single(layer => layer.ObjectId == fixture.RootId);
        Assert.Equal(new CompositionRect(-4, -2, 24, 14), owner.ContentBounds);
        using var image = Decode(owner);
        Assert.Equal(SKColors.Magenta, image.GetPixel(0, 0));
        var customLayer = Assert.Single(snapshot.Layers, layer => layer.ObjectId is null);
        Assert.Equal(owner.CaptureId, customLayer.ParentCaptureId);
        using var customImage = Decode(customLayer);
        Assert.Equal(SKColors.Cyan, customImage.GetPixel(3, 3));
        Assert.Equal(25, customLayer.Transform.OffsetX);
        Assert.Equal(12, customLayer.Transform.OffsetY);
    }

    [AvaloniaFact]
    public async Task Node_depth_pixel_byte_and_wire_budgets_report_real_omissions_and_exclude_highlight()
    {
        var root = new Canvas
        {
            Width = 120, Height = 90, Background = Brushes.Blue,
            Children =
            {
                new Border { Width = 30, Height = 20, Background = Brushes.Red },
                new HighlightAdorner { Width = 50, Height = 40 }
            }
        };
        using var fixture = new Fixture(root);
        var complete = await fixture.Capture();
        Assert.Equal(2, complete.Layers.Count);
        var depth = await fixture.Capture(new CompositionSnapshotRequest(fixture.RootId, MaxDepth: 0));
        Assert.Single(depth.Layers);
        Assert.True(depth.Truncated);
        var nodes = await fixture.Capture(new CompositionSnapshotRequest(fixture.RootId, MaxNodes: 1));
        Assert.Single(nodes.Layers);
        Assert.True(nodes.Truncated);
        var pixels = await fixture.Capture(new CompositionSnapshotRequest(fixture.RootId, MaxTotalPixels: 1));
        Assert.All(pixels.Layers, layer => Assert.Equal("PixelBudgetExceeded", layer.ImageStatus));
        Assert.False(pixels.SupportsExactComposite);
        var bytes = await fixture.Capture(new CompositionSnapshotRequest(fixture.RootId, MaxImageBytes: 1));
        Assert.All(bytes.Layers, layer => Assert.Equal("ImageBudgetExceeded", layer.ImageStatus));
        Assert.All(bytes.Layers, layer => Assert.Null(layer.PngBase64));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(complete, ProtocolJson.Options).Length < RpcPeer.MaximumFrameBytes - 4096);
    }

    [AvaloniaFact]
    public async Task Empty_nodes_are_retained_and_unsupported_effects_are_explicit()
    {
        var root = new Canvas
        {
            Width = 120, Height = 90,
            Children = { new Border { Width = 30, Height = 20, Background = Brushes.Red, Effect = new BlurEffect { Radius = 2 } } }
        };
        using var fixture = new Fixture(root);
        var snapshot = await fixture.Capture();
        Assert.Equal("Empty", snapshot.Layers[0].ImageStatus);
        Assert.Null(snapshot.Layers[0].PngBase64);
        Assert.True(snapshot.Layers[1].HasEffect);
        Assert.False(snapshot.SupportsExactComposite);
        Assert.Contains(snapshot.Limitations, message => message.StartsWith("Effects", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public async Task Geometry_paths_rounded_bounds_and_unavailable_stream_paths_preserve_clip_metadata()
    {
        var path = new Border
        {
            Name = "Path", Width = 30, Height = 20, Background = Brushes.Red,
            Clip = PathGeometry.Parse("F1 M 0,0 L 30,0 L 15,20 Z")
        };
        var rounded = new Border
        {
            Name = "Rounded", Width = 30, Height = 20, Background = Brushes.Red,
            CornerRadius = new CornerRadius(7), ClipToBounds = true
        };
        var stream = new Border
        {
            Name = "Stream", Width = 30, Height = 20, Background = Brushes.Red,
            Clip = Geometry.Parse("M 0,0 L 30,0 L 15,20 Z")
        };
        using var fixture = new Fixture(new Canvas { Width = 120, Height = 90, Children = { path, rounded, stream } });
        var snapshot = await fixture.Capture();
        var pathClip = snapshot.Layers.Single(layer => layer.Name == "Path").Clip!;
        Assert.Equal("PathNonZero", pathClip.Kind);
        Assert.NotNull(pathClip.PathData);
        Assert.False(pathClip.PathData.StartsWith("F1", StringComparison.Ordinal));
        Assert.Equal("RoundedRectangle", snapshot.Layers.Single(layer => layer.Name == "Rounded").BoundsClip!.Kind);
        Assert.Equal(new CornerRadius(7), CornerRadius.Parse(snapshot.Layers.Single(layer => layer.Name == "Rounded").BoundsClip!.CornerRadius!));
        Assert.Equal("UnavailablePath", snapshot.Layers.Single(layer => layer.Name == "Stream").Clip!.Kind);
        Assert.False(snapshot.SupportsExactComposite);
        Assert.All(snapshot.Layers.Skip(1), layer => Assert.Equal("Captured", layer.ImageStatus));
    }

    [AvaloniaFact]
    public async Task Invalid_request_detachment_cancellation_and_session_disposal_release_capture()
    {
        using var fixture = new Fixture(new Canvas { Width = 120, Height = 90 });
        foreach (var request in new[]
        {
            new CompositionSnapshotRequest(fixture.RootId, MaxNodes: 257),
            new CompositionSnapshotRequest(fixture.RootId, MaxDepth: -1),
            new CompositionSnapshotRequest(fixture.RootId, MaxImageBytes: 0),
            new CompositionSnapshotRequest(fixture.RootId, MaxTotalImageBytes: 2097153),
            new CompositionSnapshotRequest(fixture.RootId, MaxTotalPixels: 4194305),
            new CompositionSnapshotRequest(fixture.RootId, Scaling: double.NaN)
        })
            Assert.Equal("invalid_request", (await Assert.ThrowsAsync<RpcException>(() => fixture.Service.CaptureAsync(request))).Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.CaptureAsync(new(fixture.RootId), cancelled.Token));
        var pending = fixture.Service.CaptureAsync(new(fixture.RootId));
        fixture.Host.Children.Clear();
        Assert.Equal("detached_object", (await Assert.ThrowsAsync<RpcException>(() => pending)).Code);
        fixture.Service.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Service.CaptureAsync(new(fixture.RootId)));
    }

    [AvaloniaFact]
    public async Task Typed_client_capture_crosses_real_named_pipe_and_keeps_the_UI_responsive()
    {
        var root = new Canvas
        {
            Name = "CaptureTarget", Width = 120, Height = 90, Background = Brushes.Blue,
            Children = { new Border { Name = "Red", Width = 30, Height = 20, Background = Brushes.Red } }
        };
        using var fixture = new Fixture(root);
        await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
        {
            PipeName = "avdt-composition-test-" + Guid.NewGuid().ToString("N"),
            TopLevelProvider = () => [fixture.Window]
        });
        await using var client = new DiagnosticsClient();
        await client.ConnectAsync(agent.PipeName);
        var roots = await client.GetRootsAsync(TreeKind.Logical);
        var host = Assert.Single(await client.GetChildrenAsync(roots[0].Id, TreeKind.Logical),
            node => node.TypeName == typeof(Canvas).FullName);
        var target = Assert.Single(await client.GetChildrenAsync(host.Id, TreeKind.Logical));
        var pending = client.GetCompositionSnapshotAsync(target.Id);
        var ticks = 0;
        while (!pending.IsCompleted && ticks++ < 100)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(5);
        }
        var snapshot = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(target.Id, snapshot.RootObjectId);
        Assert.Equal(2, snapshot.Layers.Count);
        using var image = Decode(snapshot.Layers.Single(layer => layer.Name == "Red"));
        Assert.Equal(SKColors.Red, image.GetPixel(4, 4));
        Assert.NotNull(await client.GetRootsAsync(TreeKind.Logical));
    }

    private static SKBitmap Decode(CompositionLayer layer) => SKBitmap.Decode(Convert.FromBase64String(Assert.IsType<string>(layer.PngBase64)));
    private static SKBitmap Decode(Bitmap bitmap)
    {
        using var memory = new MemoryStream();
        bitmap.Save(memory, new PngBitmapEncoderOptions());
        return SKBitmap.Decode(memory.ToArray());
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(Control root)
        {
            RootId = Registry.GetId(root);
            Service = new CompositionInspectionService(Registry, Registry.GetId);
            Host = new Canvas { Children = { root } };
            Window = new Window { Width = 240, Height = 180, Content = Host };
            Window.Show();
            Window.UpdateLayout();
            using var frame = Window.CaptureRenderedFrame();
        }
        public WeakObjectRegistry Registry { get; } = new();
        public CompositionInspectionService Service { get; }
        public Canvas Host { get; }
        public Window Window { get; }
        public long RootId { get; }
        public async Task<CompositionSnapshot> Capture(CompositionSnapshotRequest? request = null)
        {
            var pending = Service.CaptureAsync(request ?? new(RootId));
            for (var i = 0; i < 20 && !pending.IsCompleted; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await Task.Delay(5);
            }
            return await pending;
        }
        public void Dispose() { Service.Dispose(); Window.Close(); }
    }

    private sealed class OverflowControl : Control
    {
        public override void Render(DrawingContext context) => context.FillRectangle(Brushes.Magenta, new Rect(-4, -2, 24, 14));
    }

    private sealed class CustomPaint : CompositionCustomVisualHandler
    {
        public override void OnRender(ImmediateDrawingContext drawingContext) =>
            drawingContext.DrawRectangle(Brushes.Cyan, null, new Rect(0, 0, 12, 8));
    }

    private sealed class CompositeControl(CompositionSnapshot snapshot) : Control, IDisposable
    {
        private readonly Dictionary<long, Bitmap> _images = snapshot.Layers.Where(layer => layer.PngBase64 is not null)
            .ToDictionary(layer => layer.CaptureId, layer => new Bitmap(new MemoryStream(Convert.FromBase64String(layer.PngBase64!))));
        public override void Render(DrawingContext context)
        {
            Draw(snapshot.Layers[0]);
            void Draw(CompositionLayer layer)
            {
                if (!layer.IsVisible) return;
                var m = layer.Transform;
                using var transform = context.PushTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.OffsetX, m.OffsetY));
                using var opacity = context.PushOpacity(layer.Opacity);
                using var clip = layer.ClipToBounds ? context.PushClip(new Rect(0, 0, layer.Width, layer.Height)) : default;
                using var geometryClip = layer.Clip is { Kind: "Rectangle", Bounds: { } clipBounds }
                    ? context.PushGeometryClip(new RectangleGeometry(new Rect(clipBounds.X, clipBounds.Y, clipBounds.Width, clipBounds.Height))) : default;
                if (_images.TryGetValue(layer.CaptureId, out var image) && layer.ContentBounds is { } bounds)
                    context.DrawImage(image, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
                foreach (var child in snapshot.Layers.Where(child => child.ParentCaptureId == layer.CaptureId)) Draw(child);
            }
        }
        public void Dispose() { foreach (var image in _images.Values) image.Dispose(); }
    }
}
