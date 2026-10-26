using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using SkiaSharp;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class OverlayTests
{
    [AvaloniaFact]
    public void Default_overlay_draws_only_bounds_and_never_changes_the_application_control()
    {
        var target = new Border { Name = "Subject", Width = 80, Height = 60, Background = Brushes.White };
        using var fixture = new Fixture(target);
        var before = target.Bounds;
        Assert.True(fixture.Service.Show(target));
        var snapshot = fixture.Service.Snapshot(new());
        Assert.Equal(new OverlayOptions(), snapshot.Options);
        Assert.Empty(snapshot.Rulers);
        Assert.Empty(snapshot.Guidelines);
        Assert.False(fixture.Service.Adorner!.IsHitTestVisible);
        Assert.False(fixture.Service.Adorner.Focusable);
        Assert.Null(AdornerLayer.GetAdornedElement(fixture.Service.Adorner));
        using var pixels = fixture.Pixels();
        Assert.True(snapshot.IsVisible, string.Join("; ", snapshot.Limitations));
        Assert.Equal(320, fixture.Service.Adorner.Bounds.Width);
        Assert.Equal(240, fixture.Service.Adorner.Bounds.Height);
        Assert.True(fixture.Service.Adorner.IsEffectivelyVisible,
            string.Join("; ",
                fixture.Service.Adorner.GetSelfAndVisualAncestors().Select(visual =>
                    $"{visual.GetType().Name}: {visual.IsVisible}/{visual.Opacity}/{visual.Bounds}")));
        Assert.Contains(pixels.Pixels, pixel => pixel.Blue > pixel.Red + 30 && pixel.Blue > pixel.Green + 20);
        AssertBlue(Pixel(pixels, target, fixture.Window, new Point(40, 0)));
        Assert.Equal(SKColors.White, Pixel(pixels, target, fixture.Window, new Point(-5, 20)));
        Assert.Equal(before, target.Bounds);
        Assert.True(target.IsVisible);
        Assert.Same(Brushes.White, target.Background);
    }

    [AvaloniaFact]
    public void Margin_and_padding_have_distinct_real_pixels_outside_and_inside_the_border_box()
    {
        var target = new Border
        {
            Width = 80, Height = 60, Background = Brushes.White, Margin = new Thickness(10),
            Padding = new Thickness(12), BorderThickness = new Thickness(4)
        };
        using var fixture = new Fixture(target);
        fixture.Service.Configure(new(new OverlayOptions(ShowMargin: true, ShowPadding: true), fixture.Id));
        using var pixels = fixture.Pixels();
        AssertOrange(Pixel(pixels, target, fixture.Window, new Point(-5, 25)));
        AssertGreen(Pixel(pixels, target, fixture.Window, new Point(9, 25)));
        var content = Pixel(pixels, target, fixture.Window, new Point(30, 30));
        Assert.True(content.Blue > content.Red && content.Blue > content.Green);
        var border = Pixel(pixels, target, fixture.Window, new Point(2, 25));
        Assert.True(border.Blue > border.Red && border.Blue > border.Green);
        var snapshot = fixture.Service.Snapshot(new());
        Assert.Equal(new OverlayThickness(12, 12, 12, 12), snapshot.Padding);
        Assert.Equal(12, snapshot.Regions.Count);
        Assert.Equal(4, snapshot.Regions.Count(region => region.Kind == "Border"));
        Assert.Contains(snapshot.Limitations, reason => reason.StartsWith("margin_box:", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Text_information_uses_actual_TextBlock_layout_without_returning_contents()
    {
        const string content = "private text that wraps across multiple lines for actual layout";
        var target = new TextBlock
        {
            Width = 110, Text = content, FontSize = 17, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.WordEllipsis, MaxLines = 3, LetterSpacing = 1.25
        };
        using var fixture = new Fixture(target);
        var snapshot = fixture.Service.Configure(new(new OverlayOptions(ShowInfo: true), fixture.Id));
        var info = Assert.IsType<OverlayControlInfo>(snapshot.Info!.ControlInfo);
        Assert.Equal("TextBlock", info.Kind);
        Assert.Contains(info.Fields, field => field.Name == "TextLength" && field.Value == content.Length.ToString());
        Assert.Contains(info.Fields,
            field => field.Name == "LineCount" && field.Value == target.TextLayout.TextLines.Count.ToString());
        Assert.Contains(info.Fields, field => field.Name == "FontSizeDip" && field.Value == "17");
        Assert.Contains(info.Fields, field => field.Name == "Trimming" && field.Value == "WordEllipsis");
        Assert.All(info.Fields, field => Assert.InRange(field.Value.Length, 0, 120));
        Assert.DoesNotContain(content, snapshot.Info.Text);
        Assert.DoesNotContain(info.Fields, field => field.Value.Contains(content, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void TextBox_reports_its_real_presenter_layout_and_password_skips_text_metadata()
    {
        const string secret = "Secret password never returned";
        var target = new TextBox
            { Width = 110, Height = 80, Text = secret, FontSize = 16, TextWrapping = TextWrapping.Wrap };
        using var fixture = new Fixture(target);
        var normal = fixture.Service.Configure(new(new OverlayOptions(ShowInfo: true), fixture.Id)).Info!.ControlInfo!;
        Assert.Contains(normal.Fields,
            field => field.Name == "LayoutSource" && field.Value == "PART_TextPresenter.TextLayout");
        Assert.Contains(normal.Fields, field => field.Name == "LineCount" && int.Parse(field.Value) >= 1);
        target.PasswordChar = '*';
        var password = fixture.Service.Snapshot(new()).Info!;
        Assert.Contains(password.ControlInfo!.Fields,
            field => field.Name == "TextLength" && field.Value == "unknown" && field.Status == "redacted_password");
        Assert.Contains(password.ControlInfo.Fields,
            field => field.Name == "Layout" && field.Status == "redacted_password");
        Assert.DoesNotContain(password.ControlInfo.Fields, field => field.Name == "LineCount");
        Assert.DoesNotContain(secret, password.Text);
        Assert.DoesNotContain(new string('*', secret.Length), password.Text);
    }

    [AvaloniaFact]
    public void Bitmap_information_distinguishes_decoded_pixels_DPI_DIP_and_actual_source_identity()
    {
        using var source = new WriteableBitmap(new PixelSize(90, 60),
            new Vector(144, 120),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);
        var target = new Image { Source = source, Width = 120, Height = 100, Stretch = Stretch.UniformToFill };
        using var fixture = new Fixture(target);
        fixture.Window.SetRenderScaling(2);
        var info = fixture.Service.Configure(new(new OverlayOptions(ShowInfo: true), fixture.Id)).Info!.ControlInfo!;
        Assert.Equal("Image", info.Kind);
        Assert.Contains(info.Fields,
            field => field.Name == "SourceObjectId" && field.Value == fixture.Registry.GetId(source).ToString());
        Assert.Contains(info.Fields, field => field.Name == "PixelSize" && field.Value == "90 × 60 px");
        Assert.Contains(info.Fields, field => field.Name == "Dpi" && field.Value == "144 × 120 dpi");
        Assert.Contains(info.Fields, field => field.Name == "SourceSizeDip" && field.Value == "60 × 48 DIP");
        Assert.Contains(info.Fields, field => field.Name == "Stretch" && field.Value == "UniformToFill");
        Assert.Contains(info.Fields, field => field.Name == "SourceUri" && field.Value == "unknown");
    }

    [AvaloniaFact]
    public void Custom_image_source_and_custom_text_layout_do_not_execute_arbitrary_getters_or_ToString()
    {
        var source = new UnsafeImage();
        var info = OverlayControlInformation.Capture(new Image { Source = source }, _ => 123)!;
        Assert.False(source.WasRead);
        Assert.Contains(info.Fields, field => field.Name == "PixelSize" && field.Status == "unsupported_source_type");
        var custom = new CustomTextBlock { Text = "test" };
        var textInfo = OverlayControlInformation.Capture(custom, null)!;
        Assert.False(custom.LayoutRead);
        Assert.Contains(textInfo.Fields,
            field => field.Name == "Layout" && field.Status == "unmeasured_or_custom_layout");
    }

    [AvaloniaFact]
    public void Box_region_emphasis_draws_only_matching_real_transformed_pixels_and_restores_options()
    {
        var target = new Border
        {
            Width = 100, Height = 80, Margin = new Thickness(10), Padding = new Thickness(12),
            BorderThickness = new Thickness(6), Background = Brushes.White, BorderBrush = Brushes.White,
            RenderTransformOrigin = RelativePoint.Center, RenderTransform = new RotateTransform(12)
        };
        using var fixture = new Fixture(target);
        fixture.Window.SetRenderScaling(1.5);
        var preferences =
            new OverlayOptions(ShowMargin: true, ShowPadding: true, ShowRulers: true, ShowGuidelines: true);
        fixture.Service.Configure(new(preferences with { EmphasizedRegion = "Margin", EmphasizedSide = "Left" },
            fixture.Id));
        using (var pixels = fixture.Pixels())
        {
            AssertOrange(Pixel(pixels, target, fixture.Window, new Point(-5, 40)));
            Assert.Equal(SKColors.White, Pixel(pixels, target, fixture.Window, new Point(105, 40)));
            Assert.Equal(SKColors.White, Pixel(pixels, target, fixture.Window, new Point(50, 40)));
        }

        fixture.Service.Configure(new(preferences with { EmphasizedRegion = "Border", EmphasizedSide = "Left" }));
        using (var pixels = fixture.Pixels())
        {
            var border = Pixel(pixels, target, fixture.Window, new Point(3, 40));
            Assert.True(border.Red > border.Blue + 30 && border.Green > border.Blue + 30);
            Assert.Equal(SKColors.White, Pixel(pixels, target, fixture.Window, new Point(97, 40)));
            Assert.Equal(SKColors.White, Pixel(pixels, target, fixture.Window, new Point(12, 40)));
        }

        fixture.Service.Configure(new(preferences with { EmphasizedRegion = "Padding", EmphasizedSide = "Left" }));
        using (var pixels = fixture.Pixels())
        {
            AssertGreen(Pixel(pixels, target, fixture.Window, new Point(12, 40)));
            Assert.Equal(SKColors.White, Pixel(pixels, target, fixture.Window, new Point(88, 40)));
        }

        var snapshot = fixture.Service.Configure(new(preferences with { EmphasizedRegion = "Content" }));
        Assert.Equal(new OverlayRect(18, 18, 64, 44), snapshot.ContentLocalRect);
        using (var pixels = fixture.Pixels())
            AssertBlue(Pixel(pixels, target, fixture.Window, new Point(50, 40)));
        var restored = fixture.Service.Configure(new(preferences));
        Assert.Null(restored.Options.EmphasizedRegion);
        using (var pixels = fixture.Pixels())
            AssertOrange(Pixel(pixels, target, fixture.Window, new Point(-5, 40)));
        Assert.False(fixture.Service.Adorner!.IsHitTestVisible);
        Assert.Equal(new Thickness(10), target.Margin);
        Assert.Equal(new Thickness(6), target.BorderThickness);
        Assert.Equal(new Thickness(12), target.Padding);
    }

    [AvaloniaFact]
    public void Nested_translation_rotation_scaling_and_center_origin_align_actual_overlay_pixels()
    {
        var target = new Border
        {
            Name = "Rotated", Width = 80, Height = 50, Margin = new Thickness(8), Padding = new Thickness(10),
            Background = Brushes.White, RenderTransformOrigin = RelativePoint.Center,
            RenderTransform = new MatrixTransform(new Matrix(0, 1.5, -2, 0, 0, 0))
        };
        var group = new Canvas
        {
            Width = 210, Height = 150, RenderTransformOrigin = RelativePoint.TopLeft,
            RenderTransform = new ScaleTransform(1.1, 1.2), Children = { target }
        };
        Canvas.SetLeft(target, 45);
        Canvas.SetTop(target, 35);
        using var fixture = new Fixture(group);
        fixture.Service.Configure(new(new OverlayOptions(ShowMargin: true), fixture.Registry.GetId(target)));
        using (var boundsPixels = fixture.Pixels())
            AssertBlue(Pixel(boundsPixels, target, fixture.Window, new Point(40, 0)));
        fixture.Service.Configure(new(new OverlayOptions(ShowMargin: true, ShowPadding: true)));
        using var pixels = fixture.Pixels();
        AssertOrange(Pixel(pixels, target, fixture.Window, new Point(-4, 25)));
        AssertGreen(Pixel(pixels, target, fixture.Window, new Point(5, 25)));
        var expected = target.TranslatePoint(default, fixture.Window)!.Value;
        var snapshot = fixture.Service.Snapshot(new());
        Assert.Equal(expected.X, snapshot.Bounds!.TopLeft.X, 8);
        Assert.Equal(expected.Y, snapshot.Bounds.TopLeft.Y, 8);
        Assert.NotEqual(snapshot.Bounds.TopLeft.X, snapshot.Bounds.BottomLeft.X);
        Assert.NotEqual(snapshot.Bounds.TopLeft.Y, snapshot.Bounds.TopRight.Y);
    }

    [AvaloniaFact]
    public void Ancestor_geometry_clip_and_root_viewport_clip_the_source_overlay_pixels()
    {
        var target = new Border { Width = 80, Height = 60, Background = Brushes.White, Margin = new Thickness(10) };
        var group = new Canvas
        {
            Width = 90, Height = 80, ClipToBounds = true,
            Clip = new RectangleGeometry(new Rect(0, 0, 45, 80)), Children = { target }
        };
        Canvas.SetLeft(target, -10);
        using var fixture = new Fixture(group);
        fixture.Service.Configure(new(new OverlayOptions(ShowMargin: true), fixture.Registry.GetId(target)));
        var snapshot = fixture.Service.Snapshot(new());
        Assert.True(snapshot.IsClipped);
        Assert.NotEmpty(snapshot.ClipOutlines!);
        using var pixels = fixture.Pixels();
        AssertBlue(Pixel(pixels, target, fixture.Window, new Point(20, 0)));
        Assert.Equal(SKColors.White, Pixel(pixels, target, fixture.Window, new Point(60, 0)));
        Assert.Equal(SKColors.White, Pixel(pixels, target, fixture.Window, new Point(-5, 20)));
    }

    [AvaloniaFact]
    public void Negative_margin_and_overlapping_padding_are_drawn_and_reported_with_their_signed_meaning()
    {
        var target = new Border
        {
            Width = 80, Height = 60, Background = Brushes.White,
            Margin = new Thickness(-12, 10, 0, 0), Padding = new Thickness(50, 0, 50, 0)
        };
        using var fixture = new Fixture(target);
        fixture.Service.Configure(new(new OverlayOptions(ShowBounds: false, ShowMargin: true), fixture.Id));
        using (var pixels = fixture.Pixels())
        {
            var negative = Pixel(pixels, target, fixture.Window, new Point(5, 25));
            Assert.True(negative.Red > negative.Green + 30 && negative.Blue > negative.Green);
            Assert.Equal(SKColors.White, Pixel(pixels, target, fixture.Window, new Point(-5, 25)));
        }

        fixture.Service.Configure(new(new OverlayOptions(ShowBounds: false, ShowPadding: true)));
        using (var pixels = fixture.Pixels())
            AssertGreen(Pixel(pixels, target, fixture.Window, new Point(40, 25)));
        var snapshot = fixture.Service.Snapshot(new());
        Assert.Contains(snapshot.Regions,
            region => region.Kind == "Margin" && region.Side == "Left" && region.Thickness == -12 && region.IsOverlap);
        Assert.Contains(snapshot.Limitations,
            reason => reason.StartsWith("negative_margin:", StringComparison.Ordinal));
        Assert.Contains(snapshot.Limitations,
            reason => reason.StartsWith("padding_overlap:", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Options_update_without_reselection_and_rulers_distinguish_root_DIP_from_device_pixels()
    {
        var target = new Border { Name = "DpiSubject", Width = 80, Height = 60, Background = Brushes.White };
        using var fixture = new Fixture(target);
        fixture.Service.Show(target);
        var adorner = fixture.Service.Adorner;
        fixture.Window.SetRenderScaling(1.5);
        fixture.Window.UpdateLayout();
        var snapshot =
            fixture.Service.Configure(new(new OverlayOptions(ShowInfo: true, ShowRulers: true, ShowGuidelines: true)));
        Assert.Same(adorner, fixture.Service.Adorner);
        Assert.Equal(1.5, snapshot.RenderScaling);
        Assert.Equal("RootDips", snapshot.CoordinateSpace);
        Assert.Equal(320, snapshot.RootViewport.Width);
        Assert.Equal(240, snapshot.RootViewport.Height);
        Assert.Equal(2, snapshot.Rulers.Count);
        Assert.All(snapshot.Rulers, ruler => Assert.Equal(ruler.LengthDip * 1.5, ruler.LengthPixels, 8));
        Assert.Contains(snapshot.Rulers,
            ruler => ruler.Label!.Contains("DIP /", StringComparison.Ordinal) &&
                     ruler.Label.Contains("px", StringComparison.Ordinal));
        Assert.NotEmpty(snapshot.Guidelines);
        Assert.Contains(snapshot.Guidelines, line => line.Start.X == 0 && line.End.X == snapshot.RootViewport.Width);
        Assert.Contains(snapshot.Guidelines, line => line.Start.Y == 0 && line.End.Y == snapshot.RootViewport.Height);
        Assert.Equal("DpiSubject", snapshot.Info!.Name);
        Assert.Contains("80 × 60 DIP", snapshot.Info.Text);
        Assert.NotNull(snapshot.Info.ScreenPositionPixels);
        using var pixels = fixture.Pixels();
        Assert.Equal(480, pixels.Width);
        Assert.Equal(360, pixels.Height);
        Assert.Contains(Enumerable.Range(0, pixels.Width), x => IsOrangeRed(pixels.GetPixel(x, 21)));
    }

    [AvaloniaFact]
    public void Mutable_transforms_resize_parent_offsets_and_visibility_are_refreshed_without_target_property_writes()
    {
        var translate = new TranslateTransform();
        var target = new Border { Width = 80, Height = 60, RenderTransform = translate };
        using var fixture = new Fixture(target);
        fixture.Service.Show(target);
        var first = fixture.Service.Snapshot(new()).Bounds!;
        translate.X = 20;
        target.Width = 100;
        Canvas.SetTop(target, 100);
        fixture.Window.UpdateLayout();
        var current = fixture.Service.Snapshot(new());
        Assert.Equal(first.TopLeft.X + 20, current.Bounds!.TopLeft.X, 8);
        Assert.Equal(100, current.Info!.Width);
        Assert.Equal(100, current.Bounds.TopLeft.Y, 8);
        target.IsVisible = false;
        Assert.False(fixture.Service.Snapshot(new()).IsVisible);
        Assert.Same(translate, target.RenderTransform);
    }

    [AvaloniaFact]
    public void Non_affine_transforms_report_a_limitation_and_draw_no_fake_identity_bounds()
    {
        var target = new Border { Width = 80, Height = 60 };
        using var fixture = new Fixture(target);
        target.RenderTransform = new MatrixTransform(new Matrix(1, 0, 0.001, 0, 1, 0, 0, 0, 1));
        fixture.Service.Show(target);
        var snapshot = fixture.Service.Snapshot(new());
        Assert.Null(snapshot.Bounds);
        Assert.False(snapshot.IsVisible);
        Assert.Contains(snapshot.Limitations,
            reason => reason.StartsWith("unsupported_transform:", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void Unsupported_padding_does_not_invoke_an_arbitrary_CLR_getter()
    {
        var target = new MisleadingPadding { Width = 80, Height = 60 };
        using var fixture = new Fixture(target);
        var snapshot = fixture.Service.Configure(new(new OverlayOptions(ShowPadding: true), fixture.Id));
        Assert.Null(snapshot.Padding);
        Assert.False(target.WasRead);
        Assert.Contains(snapshot.Limitations,
            reason => reason.StartsWith("padding_unavailable:", StringComparison.Ordinal));
        var direct = new MisleadingRegisteredThickness { Width = 80, Height = 60 };
        using var directFixture = new Fixture(direct);
        var directSnapshot =
            directFixture.Service.Configure(new(new OverlayOptions(ShowPadding: true), directFixture.Id));
        Assert.Null(directSnapshot.Padding);
        Assert.Null(directSnapshot.Border);
        Assert.False(direct.WasRead);
    }

    [AvaloniaFact]
    public void Root_cross_window_clear_detach_and_window_close_reuse_the_surface_without_retaining_targets()
    {
        var target = new Border { Width = 80, Height = 60 };
        using var fixture = new Fixture(target);
        Assert.True(fixture.Service.Show(fixture.Window));
        Assert.Equal(320, fixture.Service.Snapshot(new()).Info!.Width);
        var adorner = fixture.Service.Adorner;
        Assert.True(fixture.Service.Show(target));
        Assert.Same(adorner, fixture.Service.Adorner);
        fixture.Host.Children.Remove(target);
        Assert.Null(fixture.Service.CurrentTarget);
        Assert.False(fixture.Service.Snapshot(new()).IsActive);
        Assert.Null(AdornerLayer.GetAdornedElement(adorner!));

        var other = new Border { Width = 30, Height = 20 };
        var second = new Window { Width = 160, Height = 120, Content = other };
        second.Show();
        second.UpdateLayout();
        Assert.True(fixture.Service.Show(other));
        Assert.Same(adorner, fixture.Service.Adorner);
        Assert.Equal(160, fixture.Service.Snapshot(new()).RootViewport.Width);
        second.Close();
        Assert.Null(fixture.Service.CurrentTarget);
        Assert.False(fixture.Service.Snapshot(new()).IsActive);
    }

    [AvaloniaFact]
    public void Clear_and_detach_allow_selected_control_to_be_collected_while_the_service_remains_alive()
    {
        using var fixture = new Fixture(new Border { Width = 20, Height = 20 });
        var weak = AddAndRemoveTemporaryTarget(fixture);
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(weak.IsAlive);
        Assert.Null(fixture.Service.CurrentTarget);
        GC.KeepAlive(fixture.Service);
    }

    [AvaloniaFact]
    public void Nested_suspensions_hide_only_the_overlay_and_clear_does_not_resurrect_it()
    {
        var target = new Border { Width = 80, Height = 60, Background = Brushes.White };
        using var fixture = new Fixture(target);
        fixture.Service.Show(target);
        using (fixture.Service.Suspend())
        {
            using (fixture.Service.Suspend())
                Assert.False(fixture.Service.Adorner!.IsVisible);
            Assert.False(fixture.Service.Adorner!.IsVisible);
            using var pixels = fixture.Pixels();
            Assert.Equal(SKColors.White, Pixel(pixels, target, fixture.Window, new Point(40, 0)));
            Assert.True(target.IsVisible);
        }

        Assert.True(fixture.Service.Adorner!.IsVisible);
        using (fixture.Service.Suspend())
            fixture.Service.Clear();
        Assert.False(fixture.Service.Adorner.IsVisible);
        Assert.Null(AdornerLayer.GetAdornedElement(fixture.Service.Adorner));
    }

    [AvaloniaFact]
    public void Popup_uses_its_actual_presentation_root_and_topmost_surface_then_clears_when_closed()
    {
        using var fixture = new Fixture(new Border { Width = 80, Height = 60 });
        var target = new Border { Name = "PopupSubject", Width = 100, Height = 70, Background = Brushes.White };
        var popup = new Popup { Child = target, PlacementTarget = fixture.Host.Children[0] };
        fixture.Host.Children.Add(popup);
        popup.IsOpen = true;
        fixture.Window.UpdateLayout();
        var root = TopLevel.GetTopLevel(target);
        Assert.NotNull(root);
        Assert.Equal(popup.IsUsingOverlayLayer, ReferenceEquals(fixture.Window, root));
        root.UpdateLayout();
        Assert.True(fixture.Service.Show(target));
        var snapshot = fixture.Service.Snapshot(new());
        Assert.Equal(fixture.Registry.GetId(root), snapshot.RootObjectId);
        Assert.Contains(OverlayService.GetLayer(target, root)!.Children,
            child => ReferenceEquals(child, fixture.Service.Adorner));
        root.UpdateLayout();
        var updated = fixture.Service.Snapshot(new());
        Assert.True(updated.IsVisible, string.Join("; ", updated.Limitations));
        Assert.True(fixture.Service.Adorner!.Bounds.Width > 0,
            string.Join("; ",
                fixture.Service.Adorner.GetSelfAndVisualAncestors().Select(visual =>
                    $"{visual.GetType().Name}: {visual.Bounds}/{visual.IsVisible}/{visual.ClipToBounds}")));
        using var previous = root.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
        using var frame = root.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using (var memory = new MemoryStream())
        {
            frame.Save(memory, new PngBitmapEncoderOptions());
            using var pixels = SKBitmap.Decode(memory.ToArray());
            var point = target.TranslatePoint(new Point(50, 0), root)!.Value;
            Assert.Contains(pixels.Pixels, pixel => pixel.Blue > pixel.Red + 30 && pixel.Blue > pixel.Green + 20);
            Assert.True(point.X >= 0 && point.X < pixels.Width && point.Y >= 0 && point.Y < pixels.Height,
                $"Point: {point}; root: {root.Bounds}; target: {target.Bounds}");
            var pixel = pixels.GetPixel((int)Math.Round(point.X * root.RenderScaling),
                (int)Math.Round(point.Y * root.RenderScaling));
            Assert.True(pixel.Blue > pixel.Red + 30 && pixel.Blue > pixel.Green + 20,
                $"Point {point} scale {root.RenderScaling} pixel {pixel}; initial {snapshot.Bounds} updated {updated.Bounds}; targetBounds {target.Bounds}; clips {string.Join(';', updated.ClipOutlines!)}");
        }

        popup.IsOpen = false;
        Assert.Null(fixture.Service.CurrentTarget);
        Assert.False(fixture.Service.Snapshot(new()).IsActive);
    }

    [AvaloniaFact]
    public void Closing_the_initial_root_itself_clears_selection_and_releases_the_surface()
    {
        using var fixture = new Fixture(new Border { Width = 80, Height = 60 });
        fixture.Service.Show(fixture.Window);
        Assert.Same(fixture.Window, fixture.Service.CurrentTarget);
        var adorner = fixture.Service.Adorner;
        fixture.Window.Close();
        Assert.Null(fixture.Service.CurrentTarget);
        Assert.False(fixture.Service.Snapshot(new()).IsActive);
        Assert.Null(adorner!.GetVisualParent());
        Assert.Null(AdornerLayer.GetAdornedElement(adorner!));
    }

    [AvaloniaFact]
    public async Task Typed_client_configures_live_overlay_while_screenshots_search_and_layout_exclude_it()
    {
        var target = new Border { Name = "ClientOverlaySubject", Width = 80, Height = 60, Background = Brushes.White };
        using var fixture = new Fixture(target);
        await using var agent = DiagnosticsAgent.Attach(Application.Current!,
            new DiagnosticsAgentOptions
            {
                PipeName = "overlay-tests-" + Guid.NewGuid().ToString("N"),
                ApplicationName = "Overlay tests", TopLevelProvider = () => [fixture.Window]
            });
        await using var client = new DiagnosticsClient();
        await client.ConnectAsync(agent.PipeName);
        var root = Assert.Single(await client.GetRootsAsync(TreeKind.Visual));
        var selected =
            Assert.Single((await client.SearchTreeAsync(new("ClientOverlaySubject", TreeKind.Visual))).Matches);
        var before = await client.CaptureElementAsync(root.Id);
        var configured = await client.ConfigureOverlayAsync(new OverlayOptions(ShowMargin: true,
                ShowPadding: true,
                ShowInfo: true,
                ShowRulers: true,
                ShowGuidelines: true),
            selected.Id);
        Assert.Equal(selected.Id, configured.ObjectId);
        Assert.Equal("ClientOverlaySubject", configured.Info!.Name);
        Assert.True((await client.GetOverlaySnapshotAsync()).Options.ShowRulers);
        Assert.Empty((await client.SearchTreeAsync(new("HighlightAdorner", TreeKind.Visual))).Matches);
        var layout = await client.GetLayoutSnapshotAsync(root.Id);
        Assert.DoesNotContain(layout.Layers,
            layer => layer.TypeName.Contains(nameof(HighlightAdorner), StringComparison.Ordinal));
        var after = await client.CaptureElementAsync(root.Id);
        Assert.Equal(before.PngBase64, after.PngBase64);
        Assert.True((await client.GetOverlaySnapshotAsync()).IsActive);
        await client.HighlightAsync(null);
        Assert.False((await client.GetOverlaySnapshotAsync()).IsActive);
    }

    [AvaloniaFact]
    public async Task
        Public_overlay_service_rejects_wrong_threads_expired_ids_detached_controls_and_diagnostic_controls()
    {
        using var fixture = new Fixture(new Border { Width = 80, Height = 60 });
        Assert.Equal("object_expired",
            Assert.Throws<RpcException>(() => fixture.Service.Snapshot(new(long.MaxValue))).Code);
        var detached = new Border();
        Assert.Equal("detached_object",
            Assert.Throws<RpcException>(() => fixture.Service.Configure(new(new(), fixture.Registry.GetId(detached))))
                .Code);
        fixture.Service.Show((Control)fixture.Host.Children[0]);
        Assert.True(OverlayService.IsDiagnosticVisual(fixture.Service.Adorner));
        Assert.Equal("unsupported_object",
            Assert.Throws<RpcException>(() =>
                fixture.Service.Snapshot(new(fixture.Registry.GetId(fixture.Service.Adorner!)))).Code);
        await Task.Run(() => Assert.Throws<InvalidOperationException>(() => fixture.Service.Snapshot(new())));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddAndRemoveTemporaryTarget(Fixture fixture)
    {
        var target = new Border { Width = 20, Height = 20 };
        fixture.Host.Children.Add(target);
        fixture.Window.UpdateLayout();
        fixture.Service.Show(target);
        var weak = new WeakReference(target);
        fixture.Service.Clear();
        fixture.Host.Children.Remove(target);
        fixture.Window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        using var rendered = fixture.Window.CaptureRenderedFrame();
        return weak;
    }

    private static SKColor Pixel(SKBitmap pixels, Visual target, Window window, Point point)
    {
        var root = target.TranslatePoint(point, window)!.Value;
        return pixels.GetPixel((int)Math.Round(root.X * window.RenderScaling),
            (int)Math.Round(root.Y * window.RenderScaling));
    }

    private static bool IsOrangeRed(SKColor color) => color.Red > 200 && color.Green < 160 && color.Blue < 100;

    private static void AssertBlue(SKColor color) =>
        Assert.True(color.Blue > color.Red + 30 && color.Blue > color.Green + 20, color.ToString());

    private static void AssertOrange(SKColor color) =>
        Assert.True(color.Red > color.Green + 20 && color.Green > color.Blue + 20, color.ToString());

    private static void AssertGreen(SKColor color) =>
        Assert.True(color.Green > color.Red + 15 && color.Green > color.Blue + 15, color.ToString());

    private sealed class MisleadingPadding : Control
    {
        public bool WasRead { get; private set; }

        public Thickness Padding
        {
            get
            {
                WasRead = true;
                throw new InvalidOperationException("Must not invoke arbitrary getters.");
            }
        }
    }

    private sealed class MisleadingRegisteredThickness : Control
    {
        public static readonly DirectProperty<MisleadingRegisteredThickness, Thickness> PaddingProperty =
            AvaloniaProperty.RegisterDirect<MisleadingRegisteredThickness, Thickness>("Padding",
                control => control.Read());

        public static readonly DirectProperty<MisleadingRegisteredThickness, Thickness> BorderThicknessProperty =
            AvaloniaProperty.RegisterDirect<MisleadingRegisteredThickness, Thickness>("BorderThickness",
                control => control.Read());

        public bool WasRead { get; private set; }

        private Thickness Read()
        {
            WasRead = true;
            throw new InvalidOperationException("Unsafe direct property getter");
        }
    }

    private sealed class UnsafeImage : IImage
    {
        public bool WasRead { get; private set; }

        public Size Size
        {
            get
            {
                WasRead = true;
                throw new InvalidOperationException("Unsafe image getter");
            }
        }

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect) =>
            throw new InvalidOperationException();

        public override string ToString()
        {
            WasRead = true;
            throw new InvalidOperationException("Unsafe ToString");
        }
    }

    private sealed class CustomTextBlock : TextBlock
    {
        public bool LayoutRead { get; private set; }

        protected override TextLayout CreateTextLayout(string? text)
        {
            LayoutRead = true;
            throw new InvalidOperationException("Unsafe custom layout");
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(Control target)
        {
            Registry = new WeakObjectRegistry();
            Id = Registry.GetId(target);
            Service = new OverlayService(Registry, Registry.GetId);
            Host = new Canvas { Background = Brushes.White, Children = { target } };
            Canvas.SetLeft(target, 80);
            Canvas.SetTop(target, 90);
            Window = new Window { Width = 320, Height = 240, Content = Host };
            Window.Show();
            Window.UpdateLayout();
            using var frame = Window.CaptureRenderedFrame();
        }

        public WeakObjectRegistry Registry { get; }
        public OverlayService Service { get; }
        public Canvas Host { get; }
        public Window Window { get; }
        public long Id { get; }

        public SKBitmap Pixels()
        {
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            using var previous = Window.CaptureRenderedFrame();
            Dispatcher.UIThread.RunJobs();
            using var frame = Window.CaptureRenderedFrame()!;
            using var memory = new MemoryStream();
            frame.Save(memory, new PngBitmapEncoderOptions());
            return SKBitmap.Decode(memory.ToArray());
        }

        public void Dispose()
        {
            Service.Dispose();
            Window.Close();
        }
    }
}
