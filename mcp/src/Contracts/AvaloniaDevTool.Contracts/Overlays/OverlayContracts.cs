namespace AvaloniaDevTool.Protocol;

public static class OverlayMethods
{
    public const string Configure = "overlay.configure";
    public const string Snapshot = "overlay.snapshot";
}

/// <summary>Options persist when the highlighted target changes. Bounds are enabled by default.</summary>
public sealed record OverlayOptions(
    bool ShowBounds = true,
    bool ShowMargin = false,
    bool ShowPadding = false,
    bool ShowInfo = false,
    bool ShowRulers = false,
    bool ShowGuidelines = false,
    string? EmphasizedRegion = null,
    string? EmphasizedSide = null);

public sealed record OverlayConfigureRequest(OverlayOptions Options, long? ObjectId = null);

public sealed record OverlaySnapshotRequest(long? ObjectId = null);

public sealed record OverlayPoint(double X, double Y);

public sealed record OverlayRect(double X, double Y, double Width, double Height);

public sealed record OverlayThickness(double Left, double Top, double Right, double Bottom);

public sealed record OverlayQuad(
    OverlayPoint TopLeft,
    OverlayPoint TopRight,
    OverlayPoint BottomRight,
    OverlayPoint BottomLeft);

/// <summary>A signed box-model strip; negative margin denotes overlap inside the border box.</summary>
public sealed record OverlayBoxRegion(string Kind, string Side, double Thickness, bool IsOverlap, OverlayQuad Quad);

/// <summary>Root-space line. LengthDip and LengthPixels distinguish logical and device distances.</summary>
public sealed record OverlayLine(
    string Kind,
    OverlayPoint Start,
    OverlayPoint End,
    double LengthDip,
    double LengthPixels,
    string? Label = null);

public sealed record OverlayRulerTick(string Orientation, double PositionDip, double PositionPixels, bool IsMajor);

public sealed record OverlayInfoField(string Name, string Value, string Status = "known");

public sealed record OverlayControlInfo(string Kind, IReadOnlyList<OverlayInfoField> Fields);

public sealed record OverlayInfo(
    string TypeName,
    string? Name,
    double Width,
    double Height,
    OverlayPoint RootPosition,
    OverlayPoint? ScreenPositionPixels,
    string Text,
    OverlayControlInfo? ControlInfo = null);

/// <summary>
/// Geometry is expressed in root DIPs, before clipping. Clip outlines and limitations explain visibility;
/// RenderScaling converts root DIP distances to device pixels, not to transformed local dimensions.
/// </summary>
public sealed record OverlaySnapshot(
    bool IsActive,
    long? ObjectId,
    long? RootObjectId,
    OverlayOptions Options,
    double RenderScaling,
    OverlayRect RootViewport,
    OverlayQuad? Bounds,
    OverlayThickness Margin,
    OverlayThickness? Padding,
    IReadOnlyList<OverlayBoxRegion> Regions,
    IReadOnlyList<OverlayLine> Rulers,
    IReadOnlyList<OverlayLine> Guidelines,
    OverlayInfo? Info,
    bool IsVisible,
    bool IsClipped,
    IReadOnlyList<string> Limitations,
    string CoordinateSpace = "RootDips",
    IReadOnlyList<OverlayQuad>? ClipOutlines = null,
    IReadOnlyList<OverlayRulerTick>? RulerTicks = null,
    OverlayThickness? Border = null,
    OverlayQuad? Content = null,
    OverlayRect? ContentLocalRect = null);
