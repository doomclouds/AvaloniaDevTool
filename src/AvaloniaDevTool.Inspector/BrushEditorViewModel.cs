using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed class BrushEditorViewModel : ObservableObject
{
    private BrushDto _draft;
    private string _baseline;
    private readonly Action _changed;
    private bool _loading;
    private BrushStopViewModel? _selectedStop;
    internal BrushEditorViewModel(BrushDto value, Action changed)
    {
        _draft = value; _baseline = JsonSerializer.Serialize(value); _changed = changed;
        AddStopCommand = new AsyncCommand(() => { AddStop(); return Task.CompletedTask; }, _ => { });
        Reload(value);
    }

    public IReadOnlyList<BrushKind> Kinds => _draft.AllowedKinds ?? Enum.GetValues<BrushKind>();
    public IReadOnlyList<string> SpreadMethods { get; } = ["Pad", "Reflect", "Repeat"];
    public ObservableCollection<BrushStopViewModel> Stops { get; } = [];
    public AsyncCommand AddStopCommand { get; }
    public BrushStopViewModel? SelectedStop { get => _selectedStop; set => Set(ref _selectedStop, value); }
    public BrushKind Kind { get => _draft.Kind; set { Update(_draft with { Kind = value }); if (value != BrushKind.Solid && Stops.Count == 0) { AddStop("#FFFF0000", 0); AddStop("#FF0000FF", 1); } } }
    public bool IsSolid => Kind == BrushKind.Solid;
    public bool IsGradient => !IsSolid;
    public bool IsLinear => Kind == BrushKind.Linear;
    public bool IsRadial => Kind == BrushKind.Radial;
    public bool HasCenter => Kind is BrushKind.Radial or BrushKind.Conic;
    public bool IsConic => Kind == BrushKind.Conic;
    public string Color { get => _draft.Color; set => Update(_draft with { Color = value }); }
    public double Opacity { get => _draft.Opacity; set => Update(_draft with { Opacity = value }); }
    public string SpreadMethod { get => _draft.SpreadMethod; set => Update(_draft with { SpreadMethod = value }); }
    public string StartPoint { get => _draft.StartPoint; set => Update(_draft with { StartPoint = value }); }
    public string EndPoint { get => _draft.EndPoint; set => Update(_draft with { EndPoint = value }); }
    public string Center { get => _draft.Center; set => Update(_draft with { Center = value }); }
    public string GradientOrigin { get => _draft.GradientOrigin; set => Update(_draft with { GradientOrigin = value }); }
    public string RadiusX { get => _draft.RadiusX; set => Update(_draft with { RadiusX = value }); }
    public string RadiusY { get => _draft.RadiusY; set => Update(_draft with { RadiusY = value }); }
    public double Angle { get => _draft.Angle; set => Update(_draft with { Angle = value }); }
    public string Transform { get => _draft.Transform ?? ""; set => Update(_draft with { Transform = string.IsNullOrWhiteSpace(value) ? null : value }); }
    public string TransformOrigin { get => _draft.TransformOrigin; set => Update(_draft with { TransformOrigin = value }); }
    public bool IsDirty => JsonSerializer.Serialize(ToDto()) != _baseline;
    public BrushDto ToDto() => _draft with { Stops = _draft.Stops is null && Stops.Count == 0 ? null : Stops.Select(stop => new BrushStopDto(stop.Color, stop.Offset)).ToArray() };
    public string Validation
    {
        get { try { _ = CreatePreview(); return ""; } catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { return "画刷草稿无效：" + ex.Message; } }
    }
    public IBrush Preview
    {
        get { try { return CreatePreview(); } catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException) { return Brushes.Transparent; } }
    }
    public string DraftSummary => "草稿预览 · 应用后才修改目标";

    internal void Reload(BrushDto value)
    {
        _loading = true;
        try
        {
            _draft = value; _baseline = JsonSerializer.Serialize(value);
            Stops.Clear();
            foreach (var stop in value.Stops ?? []) Stops.Add(new BrushStopViewModel(this, stop.Color, stop.Offset));
            SelectedStop = Stops.FirstOrDefault();
            Refresh();
        }
        finally { _loading = false; }
    }

    internal void SetColor(string value)
    {
        if (_draft.Color == value) return;
        _draft = _draft with { Color = value };
        Refresh();
    }

    private void Update(BrushDto value)
    {
        if (_loading || value == _draft) return;
        _draft = value; Changed();
    }
    internal void Changed() { if (_loading) return; Refresh(); _changed(); }
    private void Refresh()
    {
        foreach (var name in new[] { nameof(Kind), nameof(Kinds), nameof(IsSolid), nameof(IsGradient), nameof(IsLinear), nameof(IsRadial), nameof(IsConic), nameof(HasCenter), nameof(Color), nameof(Opacity), nameof(SpreadMethod), nameof(StartPoint), nameof(EndPoint), nameof(Center), nameof(GradientOrigin), nameof(RadiusX), nameof(RadiusY), nameof(Angle), nameof(Transform), nameof(TransformOrigin), nameof(Preview), nameof(Validation), nameof(IsDirty) }) Raise(name);
    }
    public void AddStop() { if (Stops.Count < 256) AddStop("#FFFFFFFF", 0.5); }
    private void AddStop(string color, double offset) { var stop = new BrushStopViewModel(this, color, offset); Stops.Add(stop); SelectedStop = stop; Changed(); }
    internal void RemoveStop(BrushStopViewModel stop) { if (Stops.Count <= 2) return; Stops.Remove(stop); SelectedStop = Stops.FirstOrDefault(); Changed(); }

    private IBrush CreatePreview()
    {
        if (!double.IsFinite(Opacity) || Opacity is < 0 or > 1) throw new FormatException("Opacity: 0–1");
        Brush brush = Kind switch
        {
            BrushKind.Solid => new SolidColorBrush(Avalonia.Media.Color.Parse(Color)),
            BrushKind.Linear => new LinearGradientBrush { StartPoint = Point(StartPoint), EndPoint = Point(EndPoint) },
            BrushKind.Radial => new RadialGradientBrush { Center = Point(Center), GradientOrigin = Point(GradientOrigin), RadiusX = Radius(RadiusX), RadiusY = Radius(RadiusY) },
            BrushKind.Conic when double.IsFinite(Angle) => new ConicGradientBrush { Center = Point(Center), Angle = Angle },
            _ => throw new FormatException("Brush kind")
        };
        brush.Opacity = Opacity; brush.TransformOrigin = Point(TransformOrigin);
        if (!string.IsNullOrWhiteSpace(Transform))
        {
            var matrix = Matrix.Parse(Transform);
            if (!new[] { matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32 }.All(double.IsFinite)) throw new FormatException("变换矩阵必须是有限数值。");
            brush.Transform = new MatrixTransform(matrix);
        }
        if (brush is GradientBrush gradient)
        {
            if (!Enum.TryParse<GradientSpreadMethod>(SpreadMethod, out var spread) || !Enum.IsDefined(spread)) throw new FormatException("SpreadMethod");
            if (Stops.Count is < 2 or > 256) throw new FormatException("色标数量：2–256");
            gradient.SpreadMethod = spread;
            foreach (var stop in Stops)
            {
                if (!double.IsFinite(stop.Offset) || stop.Offset is < 0 or > 1) throw new FormatException("Offset: 0–1");
                gradient.GradientStops.Add(new GradientStop(Avalonia.Media.Color.Parse(stop.Color), stop.Offset));
            }
        }
        return brush;
    }

    private static RelativePoint Point(string text)
    {
        var point = RelativePoint.Parse(text);
        if (!double.IsFinite(point.Point.X) || !double.IsFinite(point.Point.Y)) throw new FormatException("坐标必须是有限数值。");
        return point;
    }

    private static RelativeScalar Radius(string text)
    {
        var radius = RelativeScalar.Parse(text);
        if (!double.IsFinite(radius.Scalar) || radius.Scalar < 0) throw new FormatException("半径必须是有限非负数值。");
        return radius;
    }
}

public sealed class BrushStopViewModel : ObservableObject
{
    private readonly BrushEditorViewModel _owner;
    private string _color;
    private double _offset;
    internal BrushStopViewModel(BrushEditorViewModel owner, string color, double offset)
    {
        _owner = owner; _color = color; _offset = offset;
        RemoveCommand = new AsyncCommand(() => { owner.RemoveStop(this); return Task.CompletedTask; }, _ => { });
    }
    public string Color { get => _color; set { if (Set(ref _color, value)) { Raise(nameof(Swatch)); _owner.Changed(); } } }
    public double Offset { get => _offset; set { if (Set(ref _offset, value)) _owner.Changed(); } }
    public IBrush Swatch => Avalonia.Media.Color.TryParse(Color, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;
    public AsyncCommand RemoveCommand { get; }
}
