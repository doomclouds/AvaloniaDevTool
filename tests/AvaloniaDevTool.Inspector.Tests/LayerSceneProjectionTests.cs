using System.Numerics;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class LayerSceneProjectionTests
{
    [Fact]
    public void Local_quad_uses_all_affine_terms_in_root_coordinates()
    {
        var corners = LayerSceneProjection.TransformCorners(20, 10, new Matrix3x2(2, 1, 3, 4, 5, 6));
        Assert.Equal(new Vector2(5, 6), corners[0]);
        Assert.Equal(new Vector2(45, 26), corners[1]);
        Assert.Equal(new Vector2(75, 66), corners[2]);
        Assert.Equal(new Vector2(35, 46), corners[3]);
    }

    [Fact]
    public void Rotation_preserves_length_and_zoom_scales_about_viewport_center()
    {
        var point = new Vector3(3, 4, 12);
        var rotated = LayerSceneProjection.Rotate(point, 37, -22);
        Assert.InRange(Math.Abs(rotated.Length() - point.Length()), 0, 0.00001);
        var layers = new[] { Layer(1, 0) };
        var normal = LayerSceneProjection.Project(layers, 100, 100, 500, 400, new LayerCamera(0, 0, 1, 18))[0];
        var zoomed = LayerSceneProjection.Project(layers, 100, 100, 500, 400, new LayerCamera(0, 0, 2, 18))[0];
        var center = new Vector2(250, 200);
        Assert.InRange(Vector2.Distance((normal.Corners[0] - center) * 2, zoomed.Corners[0] - center), 0, 0.0001);
    }

    [Fact]
    public void Polygon_hit_uses_frontmost_drawn_layer_and_ignores_outside_points()
    {
        var projected = LayerSceneProjection.Project(new[] { Layer(1, 0), Layer(2, 1) },
            100, 100, 400, 400, new LayerCamera(0, 0, 1, 20));
        Assert.Equal(2, LayerSceneProjection.HitTest(projected, new Vector2(200, 200))!.Layer.ObjectId);
        Assert.Null(LayerSceneProjection.HitTest(projected, new Vector2(-10, -10)));
    }

    [Fact]
    public void Zero_sized_layers_and_viewports_produce_no_projected_quad()
    {
        Assert.Empty(LayerSceneProjection.Project(new[] { Layer(1, 0) with { Width = 0 } }, 100, 100, 400, 400, new LayerCamera()));
        Assert.Empty(LayerSceneProjection.Project(new[] { Layer(1, 0) }, 100, 100, 0, 400, new LayerCamera()));
    }

    [Theory]
    [InlineData(double.NaN, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, -1)]
    [InlineData(1, double.PositiveInfinity)]
    public void Invalid_zoom_or_depth_spacing_is_rejected(double zoom, double spacing)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayerSceneProjection.Project(
            new[] { Layer(1, 0) }, 100, 100, 400, 400, new LayerCamera(0, 0, zoom, spacing)));
    }

    [Fact]
    public void Points_at_or_in_front_of_near_plane_and_invalid_depth_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LayerSceneProjection.Perspective(new Vector3(0, 0, 10), 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayerSceneProjection.Perspective(new Vector3(0, 0, float.NaN), 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => LayerSceneProjection.Project(
            new[] { Layer(1, -1) }, 100, 100, 400, 400, new LayerCamera()));
    }

    private static LayerGeometry Layer(long id, int depth) => new(id, null, depth, "Control", null,
        100, 100, Matrix3x2.Identity);
}

