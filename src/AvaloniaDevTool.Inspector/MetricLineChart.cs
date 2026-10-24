using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AvaloniaDevTool.Inspector;

public sealed record MetricChartPoint(DateTimeOffset Timestamp, double Value);

public sealed class MetricLineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<MetricChartPoint>?> PointsProperty =
        AvaloniaProperty.Register<MetricLineChart, IReadOnlyList<MetricChartPoint>?>(nameof(Points));
    public static readonly StyledProperty<DateTimeOffset> WindowStartProperty =
        AvaloniaProperty.Register<MetricLineChart, DateTimeOffset>(nameof(WindowStart));
    public static readonly StyledProperty<DateTimeOffset> WindowEndProperty =
        AvaloniaProperty.Register<MetricLineChart, DateTimeOffset>(nameof(WindowEnd));
    private static readonly Pen LinePen = new(Brush.Parse("#6B5CE7"), 2);
    private static readonly Pen GridPen = new(Brush.Parse("#E2E6F0"), 1);
    static MetricLineChart() => AffectsRender<MetricLineChart>(PointsProperty, WindowStartProperty, WindowEndProperty);
    public IReadOnlyList<MetricChartPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public DateTimeOffset WindowStart { get => GetValue(WindowStartProperty); set => SetValue(WindowStartProperty, value); }
    public DateTimeOffset WindowEnd { get => GetValue(WindowEndProperty); set => SetValue(WindowEndProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size).Deflate(12);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        context.DrawRectangle(Brush.Parse("#F5F7FB"), null, new Rect(Bounds.Size), 6, 6);
        for (var index = 0; index <= 3; index++)
        {
            var y = bounds.Top + index * bounds.Height / 3;
            context.DrawLine(GridPen, new Point(bounds.Left, y), new Point(bounds.Right, y));
        }
        if (Points is not { Count: > 0 } points || WindowEnd <= WindowStart) return;
        var values = points.Where(point => point.Timestamp >= WindowStart && point.Timestamp <= WindowEnd &&
            double.IsFinite(point.Value)).ToArray();
        if (values.Length == 0) return;
        var minimum = values.Min(point => point.Value);
        var maximum = values.Max(point => point.Value);
        if (Math.Abs(maximum - minimum) < 0.0000001) { minimum -= 1; maximum += 1; }
        var scale = Math.Max(Math.Abs(minimum), Math.Abs(maximum));
        var range = maximum / scale - minimum / scale;
        Point At(int index) => new(bounds.Left + (values[index].Timestamp - WindowStart).Ticks * bounds.Width / (WindowEnd - WindowStart).Ticks,
            bounds.Bottom - (range == 0 ? .5 : (values[index].Value / scale - minimum / scale) / range) * bounds.Height);
        if (values.Length == 1) context.DrawEllipse(LinePen.Brush, null, At(0), 3, 3);
        for (var index = 1; index < values.Length; index++) context.DrawLine(LinePen, At(index - 1), At(index));
    }
}

