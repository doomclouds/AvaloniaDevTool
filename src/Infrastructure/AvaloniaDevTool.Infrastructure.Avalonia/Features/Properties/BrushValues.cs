using Avalonia;
using Avalonia.Media;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class BrushValues
{
    private static readonly (BrushKind Kind, Type Type)[] Types =
    [
        (BrushKind.Solid, typeof(SolidColorBrush)), (BrushKind.Linear, typeof(LinearGradientBrush)),
        (BrushKind.Radial, typeof(RadialGradientBrush)), (BrushKind.Conic, typeof(ConicGradientBrush))
    ];

    public static BrushDto? Describe(Type propertyType, object? value)
    {
        if (!typeof(IBrush).IsAssignableFrom(propertyType))
            return null;
        var allowed = Types.Where(item => propertyType.IsAssignableFrom(item.Type)).Select(item => item.Kind).ToArray();
        if (allowed.Length == 0)
            return null;
        if (value is null)
            return new BrushDto(allowed[0], AllowedKinds: allowed);
        if (value is not IBrush brush ||
            value is not (ISolidColorBrush or ILinearGradientBrush or IRadialGradientBrush or IConicGradientBrush))
            return null;
        // Only framework brush implementations are projected; arbitrary plugin getters remain opaque.
        if (brush.GetType().Assembly != typeof(Brush).Assembly)
            return null;
        var common = new BrushDto(BrushKind.Solid,
            brush.Opacity,
            Transform: brush.Transform?.Value.ToString(),
            TransformOrigin: brush.TransformOrigin.ToString(),
            AllowedKinds: allowed);
        if (brush is ISolidColorBrush solid)
            return common with { Color = solid.Color.ToString() };
        var gradient = (IGradientBrush)brush;
        if (gradient.GradientStops.Count > 256)
            return null;
        common = common with
        {
            Stops = gradient.GradientStops.Select(stop => new BrushStopDto(stop.Color.ToString(), stop.Offset))
                .ToArray(),
            SpreadMethod = gradient.SpreadMethod.ToString()
        };
        return brush switch
        {
            ILinearGradientBrush linear => common with
            {
                Kind = BrushKind.Linear, StartPoint = linear.StartPoint.ToString(),
                EndPoint = linear.EndPoint.ToString()
            },
            IRadialGradientBrush radial => common with
            {
                Kind = BrushKind.Radial, Center = radial.Center.ToString(),
                GradientOrigin = radial.GradientOrigin.ToString(), RadiusX = radial.RadiusX.ToString(),
                RadiusY = radial.RadiusY.ToString()
            },
            IConicGradientBrush conic => common with
            {
                Kind = BrushKind.Conic, Center = conic.Center.ToString(), Angle = conic.Angle
            },
            _ => null
        };
    }

    public static Brush Create(BrushDto value, Type propertyType)
    {
        var targetType = Types.FirstOrDefault(item => item.Kind == value.Kind).Type;
        if (targetType is null || !propertyType.IsAssignableFrom(targetType))
            throw new FormatException("The brush kind is incompatible with this property.");
        if (!double.IsFinite(value.Opacity) || value.Opacity is < 0 or > 1)
            throw new FormatException("Opacity must be between 0 and 1.");
        Brush brush = value.Kind switch
        {
            BrushKind.Solid => new SolidColorBrush(Color.Parse(value.Color)),
            BrushKind.Linear => new LinearGradientBrush
                { StartPoint = Point(value.StartPoint), EndPoint = Point(value.EndPoint) },
            BrushKind.Radial => new RadialGradientBrush
            {
                Center = Point(value.Center), GradientOrigin = Point(value.GradientOrigin),
                RadiusX = Radius(value.RadiusX), RadiusY = Radius(value.RadiusY)
            },
            BrushKind.Conic when double.IsFinite(value.Angle) => new ConicGradientBrush
                { Center = Point(value.Center), Angle = value.Angle },
            _ => throw new FormatException("The brush kind or angle is invalid.")
        };
        brush.Opacity = value.Opacity;
        brush.TransformOrigin = Point(value.TransformOrigin);
        if (value.Transform is { } transform)
        {
            var matrix = Matrix.Parse(transform);
            if (!new[] { matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32 }.All(double.IsFinite))
                throw new FormatException("The brush transform must be finite.");
            brush.Transform = new MatrixTransform(matrix);
        }

        if (brush is GradientBrush gradient)
        {
            if (!Enum.TryParse<GradientSpreadMethod>(value.SpreadMethod, out var spread) || !Enum.IsDefined(spread))
                throw new FormatException("The gradient spread method is invalid.");
            if (value.Stops is not { Count: >= 2 and <= 256 })
                throw new FormatException("A gradient requires 2 to 256 stops.");
            gradient.SpreadMethod = spread;
            foreach (var stop in value.Stops)
            {
                if (stop is null)
                    throw new FormatException("A gradient stop cannot be null.");
                if (!double.IsFinite(stop.Offset) || stop.Offset is < 0 or > 1)
                    throw new FormatException("Stop offsets must be between 0 and 1.");
                gradient.GradientStops.Add(new GradientStop(Color.Parse(stop.Color), stop.Offset));
            }
        }

        return brush;
    }

    private static RelativePoint Point(string text)
    {
        var point = RelativePoint.Parse(text);
        if (!double.IsFinite(point.Point.X) || !double.IsFinite(point.Point.Y))
            throw new FormatException("Brush coordinates must be finite.");
        return point;
    }

    private static RelativeScalar Radius(string text)
    {
        var radius = RelativeScalar.Parse(text);
        if (!double.IsFinite(radius.Scalar) || radius.Scalar < 0)
            throw new FormatException("Brush radii must be finite and nonnegative.");
        return radius;
    }
}
