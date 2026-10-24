namespace AvaloniaDevTool.Protocol;

public static class CompositionMethods
{
    public const string Snapshot = "composition.snapshot";
}

public sealed record CompositionSnapshotRequest(long ObjectId, int MaxNodes = 128, int MaxDepth = 12,
    int MaxImageBytes = 262144, int MaxTotalImageBytes = 2097152, long MaxTotalPixels = 2097152,
    double Scaling = 1);

public sealed record CompositionRect(double X, double Y, double Width, double Height);

public sealed record CompositionTransform(double M11, double M12, double M21, double M22,
    double OffsetX, double OffsetY);

/// <summary>Clip in the layer's local coordinates. PathData is SVG path syntax when available.</summary>
public sealed record CompositionClip(string Kind, CompositionRect Bounds, string? PathData = null,
    string? CornerRadius = null);

/// <summary>
/// PngBase64 contains only this compositor node's own drawing, before its transform, opacity,
/// clip, effects and children. ContentBounds positions that transparent PNG in local coordinates.
/// CaptureId identifies a node only within this snapshot; ObjectId is a weak application object id.
/// </summary>
public sealed record CompositionLayer(long CaptureId, long? ParentCaptureId, long? ObjectId,
    int Depth, int ChildIndex, string TypeName, string? Name, int? ZIndex,
    double Width, double Height, CompositionTransform Transform, CompositionTransform TransformToRoot,
    double Opacity, bool IsVisible, bool ClipToBounds, CompositionClip? Clip,
    CompositionRect? ContentBounds, int PixelWidth, int PixelHeight, string? PngBase64,
    string ImageStatus, bool HasEffect, bool HasOpacityMask, bool HasBitmapCache,
    CompositionClip? BoundsClip = null);

public sealed record CompositionSnapshot(long RootObjectId, double Width, double Height,
    IReadOnlyList<CompositionLayer> Layers, int Visited, bool Truncated,
    bool SupportsExactComposite, IReadOnlyList<string> Limitations,
    string CaptureKind = "CompositorOwnDrawing");
