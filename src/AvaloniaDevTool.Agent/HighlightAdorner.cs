using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>One root-space drawing surface; it never participates in picking, focus, or application layout.</summary>
internal sealed class HighlightAdorner : Control
{
    internal static readonly Color BoundsColor = Color.FromRgb(0, 170, 255);
    internal static readonly Color MarginColor = Color.FromRgb(255, 165, 0);
    internal static readonly Color NegativeMarginColor = Color.FromRgb(255, 69, 120);
    internal static readonly Color PaddingColor = Color.FromRgb(70, 180, 80);
    internal static readonly Color BorderColor = Color.FromRgb(220, 180, 40);
    internal static readonly Color ContentColor = Color.FromRgb(70, 160, 220);
    private static readonly Pen BoundsPen = new(new SolidColorBrush(BoundsColor), 2);
    private static readonly Pen RulerPen = new(Brushes.OrangeRed, 1);
    private static readonly Pen GuidelinePen = new(Brushes.MediumPurple, 1, new DashStyle([4, 4], 0));
    private OverlayGeometry? _geometry;

    public HighlightAdorner()
    {
        IsHitTestVisible = false;
        Focusable = false;
        ClipToBounds = true;
        AdornerLayer.SetIsClipEnabled(this, false);
    }

    internal void SetGeometry(OverlayGeometry geometry)
    {
        _geometry = geometry;
        InvalidateVisual();
    }

    internal void ClearGeometry()
    {
        _geometry = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_geometry is not { Snapshot.IsVisible: true } geometry) return;
        var snapshot = geometry.Snapshot;
        var emphasis = snapshot.Options.EmphasizedRegion;
        using var rootClip = context.PushClip(new Rect(new Size(snapshot.RootViewport.Width, snapshot.RootViewport.Height)));
        var clipping = new Stack<DrawingContext.PushedState>();
        try
        {
            foreach (var clip in geometry.AncestorClips) clipping.Push(context.PushGeometryClip(clip));
            if (emphasis == "Margin" || emphasis is null && snapshot.Options.ShowMargin)
                foreach (var region in snapshot.Regions.Where(region => region.Kind == "Margin" && MatchesSide(region)))
                    FillRegion(context, region, region.Thickness < 0 ? NegativeMarginColor : MarginColor);
            foreach (var clip in geometry.SelfClips) clipping.Push(context.PushGeometryClip(clip));
            if (emphasis is null && snapshot.Options.ShowBounds && snapshot.Bounds is { } bounds)
                context.DrawGeometry(new SolidColorBrush(Color.FromArgb(28, 0, 170, 255)), BoundsPen, Polygon(bounds));
            if (emphasis == "Padding" || emphasis is null && snapshot.Options.ShowPadding)
                foreach (var region in snapshot.Regions.Where(region => region.Kind == "Padding" && MatchesSide(region)))
                    FillRegion(context, region, PaddingColor);
            if (emphasis == "Border")
                foreach (var region in snapshot.Regions.Where(region => region.Kind == "Border" && MatchesSide(region)))
                    FillRegion(context, region, BorderColor);
            if (emphasis == "Content" && snapshot.Content is { } content)
                FillRegion(context, new OverlayBoxRegion("Content", "All", 0, false, content), ContentColor);
        }
        finally
        {
            while (clipping.Count > 0) clipping.Pop().Dispose();
        }
        foreach (var line in emphasis is null ? snapshot.Guidelines : [])
            context.DrawLine(GuidelinePen, Point(line.Start), Point(line.End));
        if (emphasis is null && snapshot.Options.ShowRulers)
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(225, 250, 250, 250)), null,
                new Rect(0, 0, snapshot.RootViewport.Width, 10));
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(225, 250, 250, 250)), null,
                new Rect(0, 0, 10, snapshot.RootViewport.Height));
            foreach (var tick in snapshot.RulerTicks ?? [])
            {
                var length = tick.IsMajor ? 9 : 4;
                var start = tick.Orientation == "Horizontal" ? new Point(tick.PositionDip, 0) : new Point(0, tick.PositionDip);
                var end = start + (tick.Orientation == "Horizontal" ? new Vector(0, length) : new Vector(length, 0));
                context.DrawLine(RulerPen, start, end);
            }
        }
        foreach (var ruler in emphasis is null ? snapshot.Rulers : [])
        {
            var start = Point(ruler.Start);
            var end = Point(ruler.End);
            context.DrawLine(RulerPen, start, end);
            var tick = ruler.Kind == "Horizontal" ? new Vector(0, 5) : new Vector(5, 0);
            context.DrawLine(RulerPen, start - tick, start + tick);
            context.DrawLine(RulerPen, end - tick, end + tick);
            if (ruler.Label is { } label)
                context.DrawText(Text(label, Brushes.OrangeRed), ruler.Kind == "Horizontal"
                    ? new Point(Math.Max(22, start.X), 20) : new Point(22, Math.Max(40, start.Y)));
        }
        if (emphasis is null && snapshot.Options.ShowInfo && snapshot.Info is { } info)
        {
            var text = Text(info.Text, Brushes.White);
            var x = Math.Clamp(info.RootPosition.X, 0, Math.Max(0, snapshot.RootViewport.Width - text.Width - 12));
            var y = Math.Clamp(info.RootPosition.Y - text.Height - 16, snapshot.Options.ShowRulers ? 42 : 0,
                Math.Max(snapshot.Options.ShowRulers ? 42 : 0, snapshot.RootViewport.Height - text.Height - 12));
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(230, 30, 30, 35)), null,
                new Rect(x, y, text.Width + 12, text.Height + 12), 3, 3);
            context.DrawText(text, new Point(x + 6, y + 6));
        }
        bool MatchesSide(OverlayBoxRegion region) => emphasis is null || snapshot.Options.EmphasizedSide is null ||
            region.Side == snapshot.Options.EmphasizedSide;
    }

    private static FormattedText Text(string text, IBrush brush) => new(text, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, Typeface.Default, 11, brush);
    private static void FillRegion(DrawingContext context, OverlayBoxRegion region, Color color) =>
        context.DrawGeometry(new SolidColorBrush(Color.FromArgb(110, color.R, color.G, color.B)),
            new Pen(new SolidColorBrush(color), 1), Polygon(region.Quad));
    private static Point Point(OverlayPoint point) => new(point.X, point.Y);
    private static StreamGeometry Polygon(OverlayQuad quad)
    {
        var result = new StreamGeometry();
        using var path = result.Open();
        path.BeginFigure(Point(quad.TopLeft), true);
        path.LineTo(Point(quad.TopRight));
        path.LineTo(Point(quad.BottomRight));
        path.LineTo(Point(quad.BottomLeft));
        path.EndFigure(true);
        return result;
    }

    // Retained for existing integrations. New sessions use OverlayService to track selection and options.
    public static (AdornerLayer Layer, HighlightAdorner Adorner)? Show(Control target)
    {
        if (TopLevel.GetTopLevel(target) is not { } root || AdornerLayer.GetAdornerLayer(target) is not { } layer)
            return null;
        var adorner = new HighlightAdorner();
        adorner.SetGeometry(OverlayGeometry.Capture(target, root, 0, 0, new OverlayOptions()));
        adorner.Width = root.Bounds.Width;
        adorner.Height = root.Bounds.Height;
        adorner.RenderTransformOrigin = RelativePoint.TopLeft;
        if (root.TransformToVisual(layer) is not { } transform || !OverlayGeometry.FiniteAffine(transform)) return null;
        adorner.RenderTransform = new MatrixTransform(transform);
        layer.Children.Add(adorner);
        return (layer, adorner);
    }
}

