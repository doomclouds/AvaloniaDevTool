namespace AvaloniaDevTool.Protocol;

public static class LayoutMethods
{
    public const string Snapshot = "layout.snapshot";
}

public sealed record LayoutSnapshotRequest(long ObjectId, int MaxNodes = 128, int MaxDepth = 12);

public sealed record LayoutLayer(long ObjectId, long? ParentId, int Depth, string TypeName, string? Name,
    double Width, double Height, double M11, double M12, double M21, double M22,
    double OffsetX, double OffsetY, bool IsVisible, bool IsClipped);

public sealed record LayoutSnapshot(long RootObjectId, double Width, double Height,
    IReadOnlyList<LayoutLayer> Layers, int Visited, bool Truncated, string CaptureKind = "VisualGeometry");

