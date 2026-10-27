namespace AvaloniaDevTool.Protocol;

public sealed record HighlightRequest(long? ObjectId);

public sealed record PickingRequest(bool Enabled);

public sealed record SelectionChangedNotification(long ObjectId);

public sealed record PickingChangedNotification(bool IsPicking);
