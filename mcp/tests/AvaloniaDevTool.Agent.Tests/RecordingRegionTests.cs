using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class RecordingRegionTests
{
    [AvaloniaFact]
    public void Multiple_transformed_controls_form_one_padded_window_region_without_changing_layout()
    {
        var first = new Border
        {
            Width = 40, Height = 20, RenderTransformOrigin = RelativePoint.TopLeft,
            RenderTransform = new ScaleTransform(2, 2)
        };
        var second = new Border { Width = 20, Height = 30 };
        Canvas.SetLeft(first, 20);
        Canvas.SetTop(first, 30);
        Canvas.SetLeft(second, 180);
        Canvas.SetTop(second, 100);
        using var fixture = new Fixture(new Canvas { Children = { first, second } });
        var before = first.Bounds;

        var region = ElementRecordingRegionService.ResolveGeometry([first, second], 10);

        Assert.Same(fixture.Window, region.Root);
        Assert.Equal(new Rect(10, 20, 200, 120), region.Bounds);
        Assert.False(region.IsClipped);
        Assert.Equal(before, first.Bounds);
        Assert.True(first.IsVisible);
        Assert.Null(fixture.Window.FocusManager!.GetFocusedElement());
    }

    [AvaloniaFact]
    public void Ancestor_clipping_and_window_edges_limit_the_recording_region()
    {
        var target = new Border { Width = 80, Height = 60 };
        Canvas.SetLeft(target, 20);
        Canvas.SetTop(target, 10);
        var clip = new Canvas { Width = 60, Height = 50, ClipToBounds = true, Children = { target } };
        Canvas.SetLeft(clip, 0);
        Canvas.SetTop(clip, 0);
        using var fixture = new Fixture(new Canvas { Children = { clip } });

        var region = ElementRecordingRegionService.ResolveGeometry([target], 12);

        Assert.Equal(new Rect(8, 0, 64, 62), region.Bounds);
        Assert.True(region.IsClipped);
    }

    [AvaloniaFact]
    public void Geometry_clip_transform_is_applied_once()
    {
        var target = new Border { Width = 100, Height = 100 };
        var parent = new Canvas
        {
            Clip = new RectangleGeometry(new Rect(0, 0, 40, 30)) { Transform = new TranslateTransform(10, 20) },
            Children = { target }
        };
        using var fixture = new Fixture(parent);

        var region = ElementRecordingRegionService.ResolveGeometry([target], 0);

        Assert.Equal(new Rect(10, 20, 40, 30), region.Bounds);
        Assert.True(region.IsClipped);
    }

    [AvaloniaFact]
    public void A_control_outside_its_clipping_parent_is_rejected()
    {
        var target = new Border { Width = 20, Height = 20 };
        Canvas.SetLeft(target, 100);
        var parent = new Canvas { Width = 50, Height = 50, ClipToBounds = true, Children = { target } };
        using var fixture = new Fixture(new Canvas { Children = { parent } });

        var error = Assert.Throws<RpcException>(() => ElementRecordingRegionService.ResolveGeometry([target], 0));

        Assert.Equal("recording_region_not_visible", error.Code);
    }

    [AvaloniaFact]
    public void Controls_from_different_windows_and_detached_or_hidden_controls_are_rejected()
    {
        var first = new Border { Width = 20, Height = 20 };
        var second = new Border { Width = 20, Height = 20 };
        using var one = new Fixture(first);
        using var two = new Fixture(second);
        Assert.Equal("recording_multiple_windows", Assert.Throws<RpcException>(() =>
            ElementRecordingRegionService.ResolveGeometry([first, second], 0)).Code);
        Assert.Equal("detached_object", Assert.Throws<RpcException>(() =>
            ElementRecordingRegionService.ResolveGeometry([new Border { Width = 20, Height = 20 }], 0)).Code);
        first.IsVisible = false;
        Assert.Equal("hidden_target", Assert.Throws<RpcException>(() =>
            ElementRecordingRegionService.ResolveGeometry([first], 0)).Code);
    }

    [Fact]
    public void Physical_region_rounds_outward_at_fractional_DPI_and_clamps_to_client_pixels()
    {
        var pixels = ElementRecordingRegionService.ToPixelBounds(new Rect(0.2, 2.1, 80.3, 40.2), 1.5, 120, 100);

        Assert.Equal(new RecordingPixelRect(0, 3, 120, 61), pixels);
    }

    [AvaloniaFact]
    public void Region_requests_are_bounded_before_native_window_access()
    {
        var registry = new WeakObjectRegistry();
        var service = new ElementRecordingRegionService(registry, registry.GetId);
        Assert.Equal("invalid_recording_region", Assert.Throws<RpcException>(() =>
            service.Resolve(new ElementRecordingRegionRequest([]))).Code);
        Assert.Equal("invalid_recording_region", Assert.Throws<RpcException>(() =>
            service.Resolve(new ElementRecordingRegionRequest(Enumerable.Range(1, 17).Select(value => (long)value).ToArray()))).Code);
        Assert.Equal("invalid_recording_region", Assert.Throws<RpcException>(() =>
            service.Resolve(new ElementRecordingRegionRequest([1], double.NaN))).Code);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(Control child)
        {
            Window = new Window { Width = 400, Height = 300, Content = child };
            Window.Show();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public Window Window { get; }

        public void Dispose()
        {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
