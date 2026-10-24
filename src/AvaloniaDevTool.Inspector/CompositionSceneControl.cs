using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaDevTool.Protocol;
using Vector = Avalonia.Vector;

namespace AvaloniaDevTool.Inspector;

/// <summary>Projects captured own-drawing images without mutating the target visual tree.</summary>
public sealed class CompositionSceneControl : Control
{
    public static readonly StyledProperty<CompositionScene?> SceneProperty = AvaloniaProperty.Register<CompositionSceneControl, CompositionScene?>(nameof(Scene));
    public static readonly StyledProperty<double> LayerSpacingProperty = AvaloniaProperty.Register<CompositionSceneControl, double>(nameof(LayerSpacing), 18);
    public static readonly StyledProperty<long?> SelectedCaptureIdProperty = AvaloniaProperty.Register<CompositionSceneControl, long?>(nameof(SelectedCaptureId));
    public static readonly StyledProperty<bool> ShowBordersProperty = AvaloniaProperty.Register<CompositionSceneControl, bool>(nameof(ShowBorders), true);
    public static readonly StyledProperty<bool> ShowGradientsProperty = AvaloniaProperty.Register<CompositionSceneControl, bool>(nameof(ShowGradients));
    public static readonly StyledProperty<int> MinimumDepthProperty = AvaloniaProperty.Register<CompositionSceneControl, int>(nameof(MinimumDepth));
    public static readonly StyledProperty<int> MaximumDepthProperty = AvaloniaProperty.Register<CompositionSceneControl, int>(nameof(MaximumDepth), 64);
    public static readonly StyledProperty<int> ResetVersionProperty = AvaloniaProperty.Register<CompositionSceneControl, int>(nameof(ResetVersion));
    public static readonly StyledProperty<int> FrontVersionProperty = AvaloniaProperty.Register<CompositionSceneControl, int>(nameof(FrontVersion));
    private double _yaw = -32, _pitch = 42, _zoom = 1;
    private Vector _pan;
    private Point? _press;
    private Vector _pressPan;
    private double _pressYaw, _pressPitch;
    private bool _dragged, _panning;

    static CompositionSceneControl() => AffectsRender<CompositionSceneControl>(SceneProperty, LayerSpacingProperty,
        SelectedCaptureIdProperty, ShowBordersProperty, ShowGradientsProperty, MinimumDepthProperty, MaximumDepthProperty, ResetVersionProperty, FrontVersionProperty);
    public CompositionSceneControl() { Focusable = true; ClipToBounds = true; }
    public CompositionScene? Scene { get => GetValue(SceneProperty); set => SetValue(SceneProperty, value); }
    public double LayerSpacing { get => GetValue(LayerSpacingProperty); set => SetValue(LayerSpacingProperty, value); }
    public long? SelectedCaptureId { get => GetValue(SelectedCaptureIdProperty); set => SetValue(SelectedCaptureIdProperty, value); }
    public bool ShowBorders { get => GetValue(ShowBordersProperty); set => SetValue(ShowBordersProperty, value); }
    public bool ShowGradients { get => GetValue(ShowGradientsProperty); set => SetValue(ShowGradientsProperty, value); }
    public int MinimumDepth { get => GetValue(MinimumDepthProperty); set => SetValue(MinimumDepthProperty, value); }
    public int MaximumDepth { get => GetValue(MaximumDepthProperty); set => SetValue(MaximumDepthProperty, value); }
    public int ResetVersion { get => GetValue(ResetVersionProperty); set => SetValue(ResetVersionProperty, value); }
    public int FrontVersion { get => GetValue(FrontVersionProperty); set => SetValue(FrontVersionProperty, value); }
    public LayerCamera Camera => new(_yaw, _pitch, _zoom, LayerSpacing);
    public Vector Pan => _pan;

    public IReadOnlyList<ProjectedLayer> GetProjectedLayers()
    {
        if (Scene is not { IsDisposed: false } scene || Bounds.Width <= 0 || Bounds.Height <= 0) return [];
        var identities = scene.Snapshot.Layers.ToDictionary(layer => layer.CaptureId);
        var geometry = scene.Snapshot.Layers.Select(layer =>
        {
            var transform = layer.TransformToRoot;
            return new LayerGeometry(layer.CaptureId, layer.ParentCaptureId, layer.Depth, layer.TypeName, layer.Name,
                Math.Max(layer.Width, 0.0001), Math.Max(layer.Height, 0.0001),
                new Matrix3x2((float)transform.M11, (float)transform.M12, (float)transform.M21, (float)transform.M22,
                    (float)transform.OffsetX, (float)transform.OffsetY), Ancestors(layer, identities).All(item => item.IsVisible), layer.ClipToBounds || layer.Clip is not null);
        }).ToArray();
        return LayerSceneProjection.Project(geometry, scene.Snapshot.Width, scene.Snapshot.Height,
            Bounds.Width, Bounds.Height, Camera)
            .Where(item => item.Layer.Depth >= MinimumDepth && item.Layer.Depth <= MaximumDepth)
            .Select(item => item with { Corners = item.Corners.Select(point => point + new Vector2((float)_pan.X, (float)_pan.Y)).ToArray() }).ToArray();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brush.Parse("#F2F5FB"), null, new Rect(Bounds.Size), 8, 8);
        if (Scene is not { IsDisposed: false } scene) return;
        try
        {
            var layers = scene.Snapshot.Layers.ToDictionary(layer => layer.CaptureId);
            using var viewportClip = context.PushClip(new Rect(Bounds.Size));
            foreach (var projected in GetProjectedLayers())
            {
                var layer = layers[projected.Layer.ObjectId];
                var ancestry = Ancestors(layer, layers).ToArray();
                if (ancestry.Any(item => !item.IsVisible)) continue;
                var outline = Outline(projected.Corners);
                if (ShowGradients)
                    context.DrawGeometry(new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                        GradientStops = [new GradientStop(Color.FromArgb(40, 91, 76, 230), 0), new GradientStop(Color.FromArgb(8, 20, 150, 180), 1)]
                    }, null, outline);
                var transform = CompositionProjection.MapPlane(projected.Layer.Width, projected.Layer.Height, projected.Corners);
                if (scene.Images.TryGetValue(layer.CaptureId, out var image) && layer.ContentBounds is { Width: > 0, Height: > 0 } content)
                {
                    var states = new Stack<IDisposable>();
                    try
                    {
                        foreach (var ancestor in ancestry.Reverse()) PushClip(context, ancestor, layer, transform, states);
                        states.Push(context.PushOpacity(Math.Clamp(ancestry.Aggregate(1d, (opacity, item) => opacity * item.Opacity), 0, 1)));
                        states.Push(context.PushTransform(transform));
                        context.DrawImage(image, new Rect(image.Size), new Rect(content.X, content.Y, content.Width, content.Height));
                    }
                    finally { while (states.TryPop(out var state)) state.Dispose(); }
                }
                if (ShowBorders) context.DrawGeometry(null, new Pen(Brush.Parse("#6B7E9C"), 1), outline);
                if (SelectedCaptureId == layer.CaptureId) context.DrawGeometry(null, new Pen(Brush.Parse("#E69C24"), 2.5), outline);
            }
        }
        catch (ArgumentException error) { ToolTip.SetTip(this, "合成层无法投影：" + error.Message); }
    }

    private static void PushClip(DrawingContext context, CompositionLayer ancestor, CompositionLayer layer,
        Matrix projectedTransform, Stack<IDisposable> states)
    {
        if (!ancestor.ClipToBounds && ancestor.Clip is null) return;
        var current = MatrixOf(layer.TransformToRoot);
        if (!current.TryInvert(out var inverse)) return;
        var clipTransform = new MatrixTransform(MatrixOf(ancestor.TransformToRoot) * inverse * projectedTransform);
        if (ancestor.ClipToBounds)
        {
            var boundsMetadata = ancestor.BoundsClip?.Bounds;
            var rectangle = boundsMetadata is null ? new Rect(0, 0, ancestor.Width, ancestor.Height) : new Rect(boundsMetadata.X, boundsMetadata.Y, boundsMetadata.Width, boundsMetadata.Height);
            if (ancestor.BoundsClip is { Kind: "RoundedRectangle", CornerRadius: { } radius })
            {
                var matrix = clipTransform.Value;
                if (matrix.TryInvert(out var revert))
                {
                    states.Push(context.PushTransform(matrix));
                    states.Push(context.PushClip(new RoundedRect(rectangle, CornerRadius.Parse(radius))));
                    states.Push(context.PushTransform(revert));
                }
            }
            else states.Push(context.PushGeometryClip(new RectangleGeometry(rectangle) { Transform = clipTransform }));
        }
        if (ancestor.Clip is not { } clip) return;
        var bounds = new Rect(clip.Bounds.X, clip.Bounds.Y, clip.Bounds.Width, clip.Bounds.Height);
        Geometry? geometry = clip.Kind switch
        {
            "Ellipse" => new EllipseGeometry(bounds),
            "Rectangle" => new RectangleGeometry(bounds),
            _ => clip.PathData is { Length: > 0 } path ? Geometry.Parse((clip.Kind == "PathNonZero" ? "F1 " : "") + path) : null
        };
        if (geometry is not null) { geometry.Transform = clipTransform; states.Push(context.PushGeometryClip(geometry)); }
    }

    private static Matrix MatrixOf(CompositionTransform transform) => new(transform.M11, transform.M12, transform.M21, transform.M22, transform.OffsetX, transform.OffsetY);
    private static IEnumerable<CompositionLayer> Ancestors(CompositionLayer layer, IReadOnlyDictionary<long, CompositionLayer> layers)
    {
        var remaining = 65;
        for (var current = layer; remaining-- > 0;)
        {
            yield return current;
            if (current.ParentCaptureId is not { } parent || !layers.TryGetValue(parent, out current!)) yield break;
        }
    }
    private static StreamGeometry Outline(IReadOnlyList<Vector2> corners)
    {
        var geometry = new StreamGeometry();
        using var writer = geometry.Open();
        writer.BeginFigure(new Point(corners[0].X, corners[0].Y), true);
        for (var index = 1; index < corners.Count; index++) writer.LineTo(new Point(corners[index].X, corners[index].Y));
        writer.EndFigure(true);
        return geometry;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResetVersionProperty) { _yaw = -32; _pitch = 42; _zoom = 1; _pan = default; InvalidateVisual(); }
        if (change.Property == FrontVersionProperty) { _yaw = 0; _pitch = 0; _zoom = 1; _pan = default; InvalidateVisual(); }
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed) return;
        Focus(); _press = e.GetPosition(this); _pressYaw = _yaw; _pressPitch = _pitch; _pressPan = _pan; _dragged = false;
        _panning = properties.IsRightButtonPressed || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        e.Pointer.Capture(this); e.Handled = true;
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_press is not { } start) return;
        var delta = e.GetPosition(this) - start;
        _dragged |= Math.Abs(delta.X) + Math.Abs(delta.Y) > 4;
        if (!_dragged) return;
        if (_panning) _pan = _pressPan + delta;
        else { _yaw = (_pressYaw + delta.X * 0.35) % 360; _pitch = Math.Clamp(_pressPitch + delta.Y * 0.35, -85, 85); }
        InvalidateVisual(); e.Handled = true;
    }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_press is null) return;
        var choose = !_dragged && !_panning;
        _press = null; e.Pointer.Capture(null);
        if (choose)
        {
            var point = e.GetPosition(this);
            var hit = LayerSceneProjection.HitTest(GetProjectedLayers().Where(item => item.Layer.IsVisible).ToArray(), new Vector2((float)point.X, (float)point.Y));
            SetCurrentValue(SelectedCaptureIdProperty, hit?.Layer.ObjectId);
        }
        e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _press = null; _dragged = false; }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); _zoom = Math.Clamp(_zoom * Math.Pow(1.12, e.Delta.Y), 0.15, 6); InvalidateVisual(); e.Handled = true;
    }
}
