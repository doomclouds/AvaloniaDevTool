using System.Numerics;
using Avalonia;

namespace AvaloniaDevTool.Inspector;

public static class CompositionProjection
{
    /// <summary>Maps an entire local drawing plane to its four perspective-projected corners.</summary>
    public static Matrix MapPlane(double width, double height, IReadOnlyList<Vector2> corners)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(width) || !double.IsFinite(height) || corners.Count != 4 ||
            corners.Any(point => !float.IsFinite(point.X) || !float.IsFinite(point.Y)))
            throw new ArgumentException("A projected image plane requires positive finite dimensions and four finite corners.");
        var p0 = corners[0]; var p1 = corners[1]; var p2 = corners[2]; var p3 = corners[3];
        double dx1 = p1.X - p2.X, dx2 = p3.X - p2.X, sx = p0.X - p1.X + p2.X - p3.X;
        double dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y, sy = p0.Y - p1.Y + p2.Y - p3.Y;
        double g = 0, h = 0;
        if (Math.Abs(sx) + Math.Abs(sy) > 0.00001)
        {
            var denominator = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(denominator) < 0.0000001) throw new ArgumentException("The projected plane is degenerate.");
            g = (sx * dy2 - dx2 * sy) / denominator;
            h = (dx1 * sy - sx * dy1) / denominator;
        }
        return new Matrix((p1.X - p0.X + g * p1.X) / width, (p1.Y - p0.Y + g * p1.Y) / width, g / width,
            (p3.X - p0.X + h * p3.X) / height, (p3.Y - p0.Y + h * p3.Y) / height, h / height,
            p0.X, p0.Y, 1);
    }
}
