using AvaloniaDevTool.NativeAutomation.Worker;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed class NativeScreenshotGeometryTests
{
    // These tests use synthetic physical-pixel bounds, not a captured window or live DPI context.
    private static readonly NativeBounds Window = new(0, 0, 100, 100);

    [Fact]
    public void A_null_request_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => NativeScreenshotRegion.ValidateRequest(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public void A_scoped_node_id_is_required(string? nodeId)
    {
        AssertError("invalid_argument", () => NativeScreenshotRegion.ValidateRequest(new() { NodeId = nodeId! }));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(16_000_001)]
    [InlineData(int.MaxValue)]
    public void MaxPixels_must_fit_the_supported_request_range(int maxPixels)
    {
        AssertError("invalid_argument", () => NativeScreenshotRegion.ValidateRequest(Rectangle(maxPixels: maxPixels)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8_000_000)]
    [InlineData(16_000_000)]
    public void MaxPixels_accepts_both_endpoints_and_the_default(int maxPixels)
    {
        NativeScreenshotRegion.ValidateRequest(Rectangle(maxPixels: maxPixels));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    public void Every_partial_rectangle_is_rejected(int mask)
    {
        var request = new NativeScreenshotRequest
        {
            NodeId = "scoped-node",
            X = (mask & 1) != 0 ? 0 : null,
            Y = (mask & 2) != 0 ? 0 : null,
            Width = (mask & 4) != 0 ? 1 : null,
            Height = (mask & 8) != 0 ? 1 : null
        };

        AssertError("invalid_coordinates", () => NativeScreenshotRegion.ValidateRequest(request));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Every_rectangle_component_must_be_finite(double value)
    {
        var valid = Rectangle();
        NativeScreenshotRequest[] requests =
        [valid with { X = value }, valid with { Y = value }, valid with { Width = value }, valid with { Height = value }];

        foreach (var request in requests)
            AssertError("invalid_coordinates", () => NativeScreenshotRegion.ValidateRequest(request));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.MinValue)]
    public void Both_rectangle_dimensions_must_be_positive(double value)
    {
        AssertError("invalid_coordinates", () => NativeScreenshotRegion.ValidateRequest(Rectangle(width: value)));
        AssertError("invalid_coordinates", () => NativeScreenshotRegion.ValidateRequest(Rectangle(height: value)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-double.Epsilon)]
    [InlineData(double.MinValue)]
    public void Node_local_coordinates_must_be_nonnegative(double value)
    {
        AssertError("invalid_coordinates", () => NativeScreenshotRegion.ValidateRequest(Rectangle(x: value)));
        AssertError("invalid_coordinates", () => NativeScreenshotRegion.ValidateRequest(Rectangle(y: value)));
    }

    [Fact]
    public void Omitting_the_rectangle_selects_the_complete_node_relative_to_its_window()
    {
        var node = new NativeBounds(-150, -75, 120, 80);
        var window = new NativeBounds(-200, -100, 800, 600);

        var region = NativeScreenshotRegion.Create(new() { NodeId = "scoped-node" }, node, window);

        Assert.Equal(new NativeScreenshotRegion(50, 25, 120, 80, node), region);
    }

    [Fact]
    public void A_node_local_rectangle_is_offset_from_the_scoped_window_source()
    {
        var region = NativeScreenshotRegion.Create(Rectangle(5, 7, 20, 10),
            new NativeBounds(130, 240, 50, 40), new NativeBounds(100, 200, 200, 100));

        Assert.Equal(new NativeScreenshotRegion(35, 47, 20, 10, new(135, 247, 20, 10)), region);
    }

    [Fact]
    public void Fractional_bounds_round_outward_to_include_each_intersecting_physical_pixel()
    {
        var region = NativeScreenshotRegion.Create(Rectangle(0.5, 0.5, 10.25, 5.25),
            new NativeBounds(10.25, 20.75, 40.5, 30.125), Window);

        Assert.Equal(new NativeScreenshotRegion(10, 21, 11, 6, new(10, 21, 11, 6)), region);
    }

    [Fact]
    public void Negative_screen_coordinates_preserve_floor_and_ceiling_semantics()
    {
        var region = NativeScreenshotRegion.Create(new() { NodeId = "scoped-node" },
            new NativeBounds(-1750.75, -950.25, 10.5, 20.5), new NativeBounds(-1920, -1080, 1920, 1080));

        Assert.Equal(new NativeScreenshotRegion(169, 129, 11, 22, new(-1751, -951, 11, 22)), region);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Already_scaled_physical_pixels_are_preserved_without_another_DPI_transform(double scale)
    {
        var region = NativeScreenshotRegion.Create(Rectangle(20 * scale, 12 * scale, 40 * scale, 24 * scale),
            new NativeBounds(-1600 + 100 * scale, 120 * scale, 200 * scale, 100 * scale),
            new NativeBounds(-1600, 0, 1600, 1200));

        Assert.Equal(new NativeScreenshotRegion((int)(120 * scale), (int)(132 * scale),
            (int)(40 * scale), (int)(24 * scale),
            new(-1600 + 120 * scale, 132 * scale, 40 * scale, 24 * scale)), region);
    }

    [Theory]
    [InlineData(100, 0, 1, 1)]
    [InlineData(99, 0, 2, 1)]
    [InlineData(0, 100, 1, 1)]
    [InlineData(0, 99, 1, 2)]
    [InlineData(0, 0, double.MaxValue, 1)]
    [InlineData(double.MaxValue, 0, double.MaxValue, 1)]
    public void The_requested_rectangle_must_remain_inside_the_node(double x, double y, double width, double height)
    {
        AssertError("invalid_coordinates", () => NativeScreenshotRegion.Create(Rectangle(x, y, width, height), Window, Window));
    }

    [Theory]
    [InlineData(99, 100)]
    [InlineData(100, 99)]
    [InlineData(109, 100)]
    [InlineData(100, 109)]
    [InlineData(99.75, 100)]
    [InlineData(100, 99.75)]
    public void A_region_outside_any_side_of_the_window_source_is_rejected(double x, double y)
    {
        AssertError("screenshot_region_out_of_bounds", () => NativeScreenshotRegion.Create(Rectangle(width: 2, height: 2),
            new NativeBounds(x, y, 2, 2), new NativeBounds(100, 100, 10, 10)));
    }

    [Fact]
    public void An_explicit_visible_subrectangle_can_select_a_node_that_extends_outside_its_window()
    {
        var node = new NativeBounds(80, 80, 80, 80);
        var window = new NativeBounds(100, 100, 100, 100);
        AssertError("screenshot_region_out_of_bounds", () => NativeScreenshotRegion.Create(new() { NodeId = "scoped-node" }, node, window));

        var region = NativeScreenshotRegion.Create(Rectangle(20, 20, 60, 60), node, window);

        Assert.Equal(new NativeScreenshotRegion(0, 0, 60, 60, new(100, 100, 60, 60)), region);
    }

    [Fact]
    public void Node_and_window_bounds_must_be_finite_positive_and_representable()
    {
        NativeBounds[] invalidBounds =
        [
            new(double.NaN, 0, 1, 1), new(0, double.NaN, 1, 1),
            new(0, 0, double.NaN, 1), new(0, 0, 1, double.NaN),
            new(double.PositiveInfinity, 0, 1, 1), new(0, double.NegativeInfinity, 1, 1),
            new(0, 0, double.PositiveInfinity, 1), new(0, 0, 1, double.PositiveInfinity),
            new(0, 0, 0, 1), new(0, 0, 1, 0), new(0, 0, 0, 0),
            new(0, 0, -1, 1), new(0, 0, 1, -1),
            new(double.MaxValue, 0, double.MaxValue, 1), new(0, double.MaxValue, 1, double.MaxValue),
            new((double)int.MinValue - 1, 0, 1, 1), new(0, (double)int.MinValue - 1, 1, 1),
            new(int.MaxValue, 0, 1, 1), new(0, int.MaxValue, 1, 1),
            new(0, 0, (double)int.MaxValue + 1, 1), new(0, 0, 1, (double)int.MaxValue + 1)
        ];

        foreach (var bounds in invalidBounds)
        {
            AssertError("screenshot_unavailable", () => NativeScreenshotRegion.Create(Rectangle(), bounds, Window));
            AssertError("screenshot_unavailable", () => NativeScreenshotRegion.Create(Rectangle(), Window, bounds));
        }
    }

    [Theory]
    [InlineData(0.5, 0, 10, 10)]
    [InlineData(0, 0.5, 10, 10)]
    [InlineData(0, 0, 10.5, 10)]
    [InlineData(0, 0, 10, 10.5)]
    public void The_complete_window_source_must_use_integer_physical_pixels(double x, double y, double width, double height)
    {
        AssertError("screenshot_source_limit", () => NativeScreenshotRegion.Create(Rectangle(), new NativeBounds(1, 1, 1, 1),
            new NativeBounds(x, y, width, height)));
    }

    [Fact]
    public void The_complete_source_limit_is_independent_of_the_selected_region_limit()
    {
        var request = Rectangle(maxPixels: 1);
        var node = new NativeBounds(0, 0, 1, 1);
        var region = NativeScreenshotRegion.Create(request, node, new NativeBounds(0, 0, 16_000_000, 1));
        Assert.Equal(new NativeScreenshotRegion(0, 0, 1, 1, node), region);

        AssertError("screenshot_source_limit", () => NativeScreenshotRegion.Create(request, node,
            new NativeBounds(0, 0, 16_000_001, 1)));
    }

    [Fact]
    public void MaxPixels_is_inclusive_and_applies_to_the_outward_rounded_image()
    {
        var node = new NativeBounds(0.25, 0.25, 4, 4);
        var region = NativeScreenshotRegion.Create(Rectangle(0.1, 0.1, 1, 1, maxPixels: 4), node, Window);
        Assert.Equal(new NativeScreenshotRegion(0, 0, 2, 2, new(0, 0, 2, 2)), region);

        AssertError("screenshot_pixel_limit", () => NativeScreenshotRegion.Create(Rectangle(0.1, 0.1, 1, 1, maxPixels: 3), node, Window));
    }

    [Fact]
    public void The_maximum_image_area_is_allowed_but_a_lower_request_budget_is_enforced()
    {
        var bounds = new NativeBounds(0, 0, 4000, 4000);
        var request = new NativeScreenshotRequest { NodeId = "scoped-node", MaxPixels = 16_000_000 };
        var region = NativeScreenshotRegion.Create(request, bounds, bounds);
        Assert.Equal(new NativeScreenshotRegion(0, 0, 4000, 4000, bounds), region);

        AssertError("screenshot_pixel_limit", () => NativeScreenshotRegion.Create(request with { MaxPixels = 15_999_999 }, bounds, bounds));
        AssertError("screenshot_pixel_limit", () => NativeScreenshotRegion.Create(request with { MaxPixels = 8_000_000 }, bounds, bounds));
    }

    [Fact]
    public void A_single_pixel_at_the_bottom_right_source_edge_is_allowed()
    {
        var bounds = new NativeBounds(100, 200, 10, 20);
        var region = NativeScreenshotRegion.Create(Rectangle(9, 19, 1, 1, maxPixels: 1), bounds, bounds);

        Assert.Equal(new NativeScreenshotRegion(9, 19, 1, 1, new(109, 219, 1, 1)), region);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue - 1)]
    public void A_single_pixel_at_each_representable_screen_coordinate_extreme_is_allowed(int coordinate)
    {
        var bounds = new NativeBounds(coordinate, coordinate, 1, 1);
        var region = NativeScreenshotRegion.Create(Rectangle(maxPixels: 1), bounds, bounds);

        Assert.Equal(new NativeScreenshotRegion(0, 0, 1, 1, bounds), region);
    }

    [Fact]
    public void A_source_span_larger_than_a_pixel_integer_is_rejected_before_integer_conversion()
    {
        var request = Rectangle(maxPixels: 1);
        var window = new NativeBounds(int.MinValue, 0, (double)uint.MaxValue, 1);
        var node = new NativeBounds(int.MinValue, 0, 1, 1);

        AssertError("screenshot_source_limit", () => NativeScreenshotRegion.Create(request, node, window));
    }

    [Fact]
    public void A_positive_rectangle_lost_to_floating_point_precision_does_not_create_an_empty_image()
    {
        var node = new NativeBounds(10, 10, 10, 10);
        var request = Rectangle(width: double.Epsilon, height: double.Epsilon);

        NativeScreenshotRegion.ValidateRequest(request);
        AssertError("invalid_coordinates", () => NativeScreenshotRegion.Create(request, node, Window));
    }

    private static NativeScreenshotRequest Rectangle(double x = 0, double y = 0, double width = 1, double height = 1,
        int maxPixels = 8_000_000) => new()
    {
        NodeId = "scoped-node", X = x, Y = y, Width = width, Height = height, MaxPixels = maxPixels
    };

    private static void AssertError(string code, Action action)
    {
        var error = Assert.Throws<NativeAutomationException>(action);
        Assert.Equal(code, error.Code);
    }
}
