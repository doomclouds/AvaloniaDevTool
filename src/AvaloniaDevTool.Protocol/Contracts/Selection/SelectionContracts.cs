namespace AvaloniaDevTool.Protocol;

public static partial class Methods
{
    public const string Highlight = "selection.highlight";
    public const string SetPicking = "selection.pick";
    public const string SelectionChanged = "selection.changed";
    public const string PickingChanged = "picking.changed";
}

public sealed record HighlightRequest(long? ObjectId);

public sealed record PickingRequest(bool Enabled);

public sealed record SelectionChangedNotification(long ObjectId);

public sealed record PickingChangedNotification(bool IsPicking);
