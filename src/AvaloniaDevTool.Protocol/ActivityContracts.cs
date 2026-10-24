namespace AvaloniaDevTool.Protocol;

public static class ActivityMethods
{
    public const string Start = "activity.start";
    public const string Stop = "activity.stop";
    public const string Snapshot = "activity.snapshot";
    public const string Clear = "activity.clear";
    public const string Status = "activity.status";
    public const string EntryAdded = "activity.entry";
    public const string StateChanged = "activity.state";
}

public sealed record ActivityStartRequest(bool CaptureLogs = true, bool CaptureEvents = false,
    long? ObjectId = null, string? EventName = null, string MinimumLogLevel = "Warning", LogFilter? LogFilter = null,
    EventSelection? EventSelection = null);

public sealed record ActivityEntry(long Sequence, DateTimeOffset Timestamp, string Kind, string Level,
    string Category, string Message, long? ObjectId = null, string? EventName = null,
    string? Route = null, bool? Handled = null, long? HandlerObjectId = null, LogDetails? LogDetails = null,
    long? EventRouteId = null);

public sealed record ActivityStatus(bool IsRunning, bool CaptureLogs, bool CaptureEvents,
    int BufferedCount, long DroppedCount, string MinimumLogLevel, string? EventName, long? ObjectId,
    long BufferedBytes = 0, long ByteBudget = 0, long ByteEvictedCount = 0, long BudgetTruncatedCount = 0,
    long NotificationBufferedBytes = 0, long NotificationByteBudget = 0, long NotificationDroppedCount = 0,
    int PendingNotificationCount = 0);

