namespace AvaloniaDevTool.Protocol;

public static class RecordingRegionMethods
{
    public const string Resolve = "recording.resolve-region";
}

public sealed record ElementRecordingRegionRequest(IReadOnlyList<long> NodeIds, double PaddingDip = 12);

public sealed record RecordingPixelRect(int X, int Y, int Width, int Height);

/// <summary>One-time geometry for a recording. Client and screen rectangles use physical pixels.</summary>
public sealed record ElementRecordingRegion(
    int ProcessId,
    DateTimeOffset ProcessStartTimeUtc,
    long WindowHandle,
    long WindowObjectId,
    IReadOnlyList<long> NodeIds,
    RecordingPixelRect WindowScreenBounds,
    RecordingPixelRect ClientScreenBounds,
    RecordingPixelRect RegionClientBounds,
    RecordingPixelRect RegionScreenBounds,
    double RenderScaling,
    bool IsClipped,
    DateTimeOffset ResolvedAt,
    IReadOnlyList<string> Limitations);
