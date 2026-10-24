using System.Numerics;

namespace AvaloniaDevTool.Inspector;

public sealed record LayerGeometry(long ObjectId, long? ParentId, int Depth, string TypeName, string? Name,
    double Width, double Height, Matrix3x2 LocalToRoot, bool IsVisible = true, bool IsClipped = false);

public sealed record LayerCamera(double YawDegrees = -32, double PitchDegrees = 42, double Zoom = 1, double Spacing = 18);

public sealed record ProjectedLayer(LayerGeometry Layer, IReadOnlyList<Vector2> Corners, float CameraDepth, int SourceOrder);

/// <summary>Projects bounded visual geometry without depending on a renderer or an RPC session.</summary>
public static class LayerSceneProjection
{
    public static IReadOnlyList<Vector2> TransformCorners(double width, double height, Matrix3x2 localToRoot)
    {
        ValidateDimension(width, nameof(width));
        ValidateDimension(height, nameof(height));
        if (!Finite(localToRoot.M11, localToRoot.M12, localToRoot.M21, localToRoot.M22, localToRoot.M31, localToRoot.M32))
            throw new ArgumentOutOfRangeException(nameof(localToRoot), "The affine transform must be finite.");
        var corners = new[]
        {
            Vector2.Transform(Vector2.Zero, localToRoot),
            Vector2.Transform(new Vector2((float)width, 0), localToRoot),
            Vector2.Transform(new Vector2((float)width, (float)height), localToRoot),
            Vector2.Transform(new Vector2(0, (float)height), localToRoot)
        };
        if (corners.Any(point => !Finite(point.X, point.Y)))
            throw new ArgumentOutOfRangeException(nameof(localToRoot), "The transformed corners exceed the finite coordinate range.");
        return corners;
    }

    public static Vector3 Rotate(Vector3 point, double yawDegrees, double pitchDegrees)
    {
        if (!Finite(point.X, point.Y, point.Z) || !double.IsFinite(yawDegrees) || !double.IsFinite(pitchDegrees))
            throw new ArgumentOutOfRangeException(nameof(point), "Camera coordinates and angles must be finite.");
        var rotation = Matrix4x4.CreateRotationY((float)((yawDegrees % 360) * Math.PI / 180)) *
            Matrix4x4.CreateRotationX((float)((pitchDegrees % 360) * Math.PI / 180));
        return Vector3.Transform(point, rotation);
    }

    public static Vector2 Perspective(Vector3 point, float cameraDistance)
    {
        if (!Finite(point.X, point.Y, point.Z, cameraDistance) || cameraDistance <= 0 ||
            point.Z >= cameraDistance - Math.Max(0.0001f, cameraDistance * 0.00001f))
            throw new ArgumentOutOfRangeException(nameof(point), "A point must stay behind the positive near plane.");
        var multiplier = cameraDistance / (cameraDistance - point.Z);
        return new Vector2(point.X * multiplier, point.Y * multiplier);
    }

    public static IReadOnlyList<ProjectedLayer> Project(IReadOnlyList<LayerGeometry> layers,
        double rootWidth, double rootHeight, double viewportWidth, double viewportHeight, LayerCamera camera)
    {
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(camera);
        ValidateDimension(rootWidth, nameof(rootWidth));
        ValidateDimension(rootHeight, nameof(rootHeight));
        ValidateDimension(viewportWidth, nameof(viewportWidth));
        ValidateDimension(viewportHeight, nameof(viewportHeight));
        if (!double.IsFinite(camera.Zoom) || camera.Zoom <= 0 || camera.Zoom > 20 ||
            !double.IsFinite(camera.Spacing) || camera.Spacing < 0 || camera.Spacing > 1024 ||
            !double.IsFinite(camera.YawDegrees) || !double.IsFinite(camera.PitchDegrees))
            throw new ArgumentOutOfRangeException(nameof(camera), "Camera zoom, spacing, and rotation must be finite and bounded.");
        if (layers.Count > 256)
            throw new ArgumentOutOfRangeException(nameof(layers), "The geometry scene supports at most 256 layers.");
        if (viewportWidth == 0 || viewportHeight == 0 || layers.Count == 0)
            return [];

        var geometry = new List<(LayerGeometry Layer, IReadOnlyList<Vector2> Corners, int Order)>();
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            if (layer.Depth is < 0 or > 512)
                throw new ArgumentOutOfRangeException(nameof(layers), "Layer depth must be within 0..512.");
            var corners = TransformCorners(layer.Width, layer.Height, layer.LocalToRoot);
            if (layer.Width > 0 && layer.Height > 0)
                geometry.Add((layer, corners, index));
        }
        if (geometry.Count == 0)
            return [];

        var flat = geometry.SelectMany(item => item.Corners).ToArray();
        var minX = Math.Min(0, flat.Min(point => point.X));
        var minY = Math.Min(0, flat.Min(point => point.Y));
        var maxX = Math.Max(rootWidth, flat.Max(point => point.X));
        var maxY = Math.Max(rootHeight, flat.Max(point => point.Y));
        var maxZ = geometry.Max(item => item.Layer.Depth) * camera.Spacing;
        var center = new Vector3((float)((minX + maxX) / 2), (float)((minY + maxY) / 2), (float)(maxZ / 2));
        var extent = Math.Max(1, Math.Max(Math.Max(maxX - minX, maxY - minY), maxZ));
        var distance = (float)(extent * 4);
        var raw = geometry.Select(item =>
        {
            var points = item.Corners.Select(point => Rotate(
                new Vector3(point.X, point.Y, (float)(item.Layer.Depth * camera.Spacing)) - center,
                camera.YawDegrees, camera.PitchDegrees)).ToArray();
            return (item.Layer, Corners: points.Select(point => Perspective(point, distance)).ToArray(),
                CameraDepth: points.Average(point => point.Z), item.Order);
        }).ToArray();
        var projected = raw.SelectMany(item => item.Corners).ToArray();
        var left = projected.Min(point => point.X);
        var top = projected.Min(point => point.Y);
        var right = projected.Max(point => point.X);
        var bottom = projected.Max(point => point.Y);
        var margin = Math.Min(22, Math.Min(viewportWidth, viewportHeight) / 10);
        var scale = Math.Min((viewportWidth - margin * 2) / Math.Max(1, right - left),
            (viewportHeight - margin * 2) / Math.Max(1, bottom - top)) * camera.Zoom;
        var screenCenter = new Vector2((float)(viewportWidth / 2), (float)(viewportHeight / 2));
        var projectionCenter = new Vector2((left + right) / 2, (top + bottom) / 2);
        return raw.Select(item => new ProjectedLayer(item.Layer,
                item.Corners.Select(point => (point - projectionCenter) * (float)scale + screenCenter).ToArray(),
                item.CameraDepth, item.Order))
            .OrderBy(item => item.CameraDepth).ThenBy(item => item.SourceOrder).ToArray();
    }

    public static ProjectedLayer? HitTest(IReadOnlyList<ProjectedLayer> drawOrder, Vector2 point)
    {
        if (!Finite(point.X, point.Y))
            return null;
        for (var index = drawOrder.Count - 1; index >= 0; index--)
            if (Contains(drawOrder[index].Corners, point))
                return drawOrder[index];
        return null;
    }

    public static bool Contains(IReadOnlyList<Vector2> corners, Vector2 point)
    {
        if (corners.Count < 3 || !Finite(point.X, point.Y))
            return false;
        var sign = 0;
        var area = 0f;
        for (var index = 0; index < corners.Count; index++)
        {
            var start = corners[index];
            var end = corners[(index + 1) % corners.Count];
            area += start.X * end.Y - end.X * start.Y;
            var cross = (end.X - start.X) * (point.Y - start.Y) - (end.Y - start.Y) * (point.X - start.X);
            if (Math.Abs(cross) < 0.001f)
                continue;
            var current = cross > 0 ? 1 : -1;
            if (sign != 0 && current != sign)
                return false;
            sign = current;
        }
        return Math.Abs(area) > 0.001f;
    }

    private static bool Finite(params float[] values) => values.All(float.IsFinite);

    private static void ValidateDimension(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value > 10_000_000)
            throw new ArgumentOutOfRangeException(name, "Geometry dimensions must be finite and within 0..10,000,000.");
    }
}

