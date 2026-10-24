using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

/// <summary>A schematic diagram for reading current box values; it never edits application layout.</summary>
public sealed class BoxModelView : Control
{
    public static readonly StyledProperty<OverlaySnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<BoxModelView, OverlaySnapshot?>(nameof(Snapshot));
    private (long Id, string Region, string? Side)? _hover;
    private MainWindowViewModel? _previewOwner;
    public OverlaySnapshot? Snapshot { get => GetValue(SnapshotProperty); set => SetValue(SnapshotProperty, value); }

    static BoxModelView() => AffectsRender<BoxModelView>(SnapshotProperty);
    public BoxModelView()
    {
        MinHeight = 264;
        AutomationProperties.SetAutomationId(this, "Inspector.BoxModel");
    }
    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsFinite(availableSize.Width) ? Math.Max(260, availableSize.Width) : 420, 264);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SnapshotProperty && _hover is { } hover && Snapshot?.ObjectId != hover.Id)
            ClearHover();
        if (change.Property == DataContextProperty) ClearHover();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClearHover();
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hit = HitRegion(e.GetPosition(this));
        if (hit == _hover) return;
        if (hit is { } next && DataContext is MainWindowViewModel vm)
        {
            _hover = next; _previewOwner = vm;
            _ = vm.PreviewBoxModelRegionAsync(next.Id, next.Region, next.Side);
            InvalidateVisual();
        }
        else ClearHover();
    }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); ClearHover(); }
    private void ClearHover()
    {
        if (_hover is not { } previous) return;
        _hover = null;
        if (_previewOwner is { } vm)
            _ = vm.PreviewBoxModelRegionAsync(previous.Id, null, leavingRegion: previous.Region, leavingSide: previous.Side);
        _previewOwner = null;
        InvalidateVisual();
    }
    private (long Id, string Region, string? Side)? HitRegion(Point point)
    {
        if (Snapshot is not { IsActive: true, ObjectId: { } id }) return null;
        var boxes = Boxes();
        for (var i = 0; i < boxes.Length; i++)
        {
            if (!boxes[i].Contains(point) || i < 3 && boxes[i + 1].Contains(point)) continue;
            var region = new[] { "Margin", "Border", "Padding", "Content" }[i];
            if (region == "Border" && Snapshot.Border is null || region == "Padding" && Snapshot.Padding is null) return null;
            if (i == 3) return (id, region, null);
            var rect = boxes[i];
            var distances = new[] { point.X - rect.Left, point.Y - rect.Top, rect.Right - point.X, rect.Bottom - point.Y };
            var side = new[] { "Left", "Top", "Right", "Bottom" }[Array.IndexOf(distances, distances.Min())];
            return (id, region, side);
        }
        return null;
    }
    private Rect[] Boxes()
    {
        var outer = new Rect(4, 4, Math.Max(0, Bounds.Width - 8), 256);
        return [outer, outer.Deflate(32), outer.Deflate(62), outer.Deflate(92)];
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var boxes = Boxes();
        var colors = new[] { Color.Parse("#F6D4AB"), Color.Parse("#EBDF9F"), Color.Parse("#BDE6BC"), Color.Parse("#C4E4F4") };
        var names = new[] { "Margin", "Border", "Padding", "Content" };
        for (var i = 0; i < boxes.Length; i++)
        {
            context.DrawRectangle(new SolidColorBrush(colors[i]), new Pen(Brushes.SlateGray, .7), boxes[i]);
            DrawText(names[i], new Point(boxes[i].X + 5, boxes[i].Y + 3), 10);
        }
        if (Snapshot is not { IsActive: true } snapshot)
        {
            DrawCentered("选择真实控件以读取当前盒模型", boxes[3].Center);
            return;
        }
        var values = new OverlayThickness?[] { snapshot.Margin, snapshot.Border, snapshot.Padding };
        for (var i = 0; i < values.Length; i++)
        {
            var outer = boxes[i]; var inner = boxes[i + 1]; var value = values[i];
            DrawCentered(Number(value?.Top), new Point(outer.Center.X, (outer.Top + inner.Top) / 2));
            DrawCentered(Number(value?.Bottom), new Point(outer.Center.X, (outer.Bottom + inner.Bottom) / 2));
            DrawCentered(Number(value?.Left), new Point((outer.Left + inner.Left) / 2, outer.Center.Y));
            DrawCentered(Number(value?.Right), new Point((outer.Right + inner.Right) / 2, outer.Center.Y));
        }
        var content = snapshot.ContentLocalRect;
        DrawCentered(content is null ? "unknown" : string.Create(CultureInfo.InvariantCulture,
            $"{content.Width:0.##} × {content.Height:0.##} DIP"), boxes[3].Center);
        if (_hover is { } hover)
        {
            var index = Array.IndexOf(names, hover.Region);
            context.DrawRectangle(null, new Pen(Brushes.DarkSlateGray, 2), boxes[index]);
        }
        void DrawText(string text, Point point, double size = 11) => context.DrawText(Format(text, size), point);
        void DrawCentered(string text, Point point)
        {
            var formatted = Format(text, 11);
            context.DrawText(formatted, point - new Vector(formatted.Width / 2, formatted.Height / 2));
        }
    }
    private static string Number(double? value) => value?.ToString("0.##", CultureInfo.InvariantCulture) ?? "?";
    private static FormattedText Format(string text, double size) => new(text, CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight, Typeface.Default, size, Brushes.DarkSlateGray);
}
