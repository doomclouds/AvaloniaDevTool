namespace AvaloniaDevTool.Protocol;

public static class RecordingInputMethods
{
    public const string Configure = "input.recording-events";
    public const string Event = "input.recording-event";
}

public sealed record RecordingInputEventsRequest(bool Enabled);

/// <summary>A framework pointer event observed immediately before its routed dispatch.</summary>
public sealed record RecordingInputEvent(
    string SubscriptionId,
    long Sequence,
    string Kind,
    string Button,
    int ScreenX,
    int ScreenY,
    long WindowHandle,
    DateTimeOffset TimestampUtc,
    long MonotonicTimestamp,
    int ClickCount = 0,
    double DeltaX = 0,
    double DeltaY = 0);

public sealed record RecordingInputEventStatus(
    bool Enabled,
    string? SubscriptionId,
    long PublishedCount,
    long DroppedCount,
    long PendingCount);
