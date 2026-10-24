using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

/// <summary>Draws a camera-controlled geometry scene; it does not capture compositor pixels.</summary>
public sealed class LayerSceneControl : Control
{
    public static readonly StyledProperty<LayoutSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<LayerSceneControl, LayoutSnapshot?>(nameof(Snapshot));
    public static readonly StyledProperty<double> LayerSpacingProperty =
        AvaloniaProperty.Register<LayerSceneControl, double>(nameof(LayerSpacing), 18);
    public static readonly StyledProperty<long?> SelectedObjectIdProperty =
        AvaloniaProperty.Register<LayerSceneControl, long?>(nameof(SelectedObjectId));
    public static readonly StyledProperty<int> ResetVersionProperty =
        AvaloniaProperty.Register<LayerSceneControl, int>(nameof(ResetVersion));
    private static readonly Color[] Palette =
    [
        Color.Parse("#6B5CE7"), Color.Parse("#0F8C79"), Color.Parse("#1E8BD1"),
        Color.Parse("#BE6D34"), Color.Parse("#A54AA7"), Color.Parse("#6E8A3B")
    ];
    private double _yaw = -32, _pitch = 42, _zoom = 1;
    private Point? _press;
    private double _pressYaw, _pressPitch;
    private bool _dragged;

    static LayerSceneControl() => AffectsRender<LayerSceneControl>(
        SnapshotProperty, LayerSpacingProperty, SelectedObjectIdProperty, ResetVersionProperty);

    public LayerSceneControl()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public LayoutSnapshot? Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }
    public double LayerSpacing { get => GetValue(LayerSpacingProperty); set => SetValue(LayerSpacingProperty, value); }
    public long? SelectedObjectId { get => GetValue(SelectedObjectIdProperty); set => SetValue(SelectedObjectIdProperty, value); }
    public int ResetVersion { get => GetValue(ResetVersionProperty); set => SetValue(ResetVersionProperty, value); }

    public IReadOnlyList<ProjectedLayer> GetProjectedLayers()
    {
        if (Snapshot is not { } snapshot || Bounds.Width <= 0 || Bounds.Height <= 0)
            return [];
        var layers = snapshot.Layers.Select(layer => new LayerGeometry(layer.ObjectId, layer.ParentId, layer.Depth,
            layer.TypeName, layer.Name, layer.Width, layer.Height,
            new Matrix3x2((float)layer.M11, (float)layer.M12, (float)layer.M21, (float)layer.M22,
                (float)layer.OffsetX, (float)layer.OffsetY), layer.IsVisible, layer.IsClipped)).ToArray();
        return LayerSceneProjection.Project(layers, snapshot.Width, snapshot.Height, Bounds.Width, Bounds.Height,
            new LayerCamera(_yaw, _pitch, _zoom, LayerSpacing));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brush.Parse("#F2F5FB"), null, new Rect(Bounds.Size), 8, 8);
        IReadOnlyList<ProjectedLayer> layers;
        try { layers = GetProjectedLayers(); }
        catch (ArgumentException error)
        {
            ToolTip.SetTip(this, "几何快照无法投影：" + error.Message);
            return;
        }
        using var clipping = context.PushClip(new Rect(Bounds.Size));
        foreach (var projected in layers)
        {
            var layer = projected.Layer;
            var color = Palette[(int)(Math.Abs(layer.ObjectId % Palette.Length))];
            var fill = new SolidColorBrush(Color.FromArgb(layer.IsVisible ? (byte)54 : (byte)12, color.R, color.G, color.B));
            var outline = new SolidColorBrush(Color.FromArgb(layer.IsVisible ? (byte)190 : (byte)85, color.R, color.G, color.B));
            var pen = new Pen(outline, layer.IsClipped ? 1.6 : 1)
            {
                DashStyle = layer.IsVisible && !layer.IsClipped ? null : DashStyle.Dash
            };
            var geometry = CreateGeometry(projected.Corners);
            context.DrawGeometry(fill, pen, geometry);
            if (SelectedObjectId == layer.ObjectId)
                context.DrawGeometry(null, new Pen(Brush.Parse("#E69C24"), 3), geometry);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResetVersionProperty)
        {
            _yaw = -32;
            _pitch = 42;
            _zoom = 1;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        _press = e.GetPosition(this);
        _pressYaw = _yaw;
        _pressPitch = _pitch;
        _dragged = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_press is not { } start) return;
        var delta = e.GetPosition(this) - start;
        _dragged |= Math.Abs(delta.X) + Math.Abs(delta.Y) > 4;
        if (!_dragged) return;
        _yaw = (_pressYaw + delta.X * 0.35) % 360;
        _pitch = Math.Clamp(_pressPitch + delta.Y * 0.35, -85, 85);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_press is null) return;
        var choose = !_dragged;
        _press = null;
        e.Pointer.Capture(null);
        if (choose)
        {
            try
            {
                var point = e.GetPosition(this);
                var hit = LayerSceneProjection.HitTest(GetProjectedLayers(), new Vector2((float)point.X, (float)point.Y));
                if (hit is not null) SetCurrentValue(SelectedObjectIdProperty, hit.Layer.ObjectId);
            }
            catch (ArgumentException) { }
        }
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _press = null;
        _dragged = false;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * Math.Pow(1.12, e.Delta.Y), 0.15, 6);
        InvalidateVisual();
        e.Handled = true;
    }

    private static StreamGeometry CreateGeometry(IReadOnlyList<Vector2> corners)
    {
        var geometry = new StreamGeometry();
        using var writer = geometry.Open();
        writer.BeginFigure(new Point(corners[0].X, corners[0].Y), true);
        foreach (var corner in corners.Skip(1)) writer.LineTo(new Point(corner.X, corner.Y));
        writer.EndFigure(true);
        return geometry;
    }
}

