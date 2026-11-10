using System.Text.Json;

namespace AvaloniaDevTool.Recording;

public sealed record RecordingCrop(int X, int Y, int Width, int Height);

// Positions are physical screen pixels; markers only affect the recorded video.
public sealed record RecordingInputMarker(string Kind, double X, double Y, string? Button = null);

public sealed record RecordingStartRequest
{
    public int ProcessId { get; init; }
    public DateTimeOffset StartTimeUtc { get; init; }
    public long WindowHandle { get; init; }
    public RecordingCrop? Crop { get; init; }
    public string? OutputDirectory { get; init; }
    public int FrameRate { get; init; } = 30;
    public int Bitrate { get; init; } = 3_000_000;
    public int MaxDurationSeconds { get; init; } = 120;
    public long MaxBytes { get; init; } = 128L * 1024 * 1024;
    public bool ShowCursor { get; init; } = true;
    public bool MouseClicks { get; init; } = true;
}

// Worker messages are independent of the target application's RPC connection.
public sealed record RecordingWorkerRequest(long Id, string Command, JsonElement Payload);

public sealed record RecordingWorkerResponse(long Id,
    bool Success,
    JsonElement? Result = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record RecordingWorkerStart
{
    public required string RecordingId { get; init; }
    public int ProcessId { get; init; }
    public DateTimeOffset StartTimeUtc { get; init; }
    public long WindowHandle { get; init; }
    public required string OutputPath { get; init; }
    public RecordingCrop? Crop { get; init; }
    public int FrameRate { get; init; } = 30;
    public int Bitrate { get; init; } = 3_000_000;
    public int MaxDurationSeconds { get; init; } = 120;
    public long MaxBytes { get; init; } = 128L * 1024 * 1024;
    public bool ShowCursor { get; init; } = true;
    public bool MouseClicks { get; init; } = true;
    public int ParentProcessId { get; init; }
    public DateTimeOffset? ParentStartTimeUtc { get; init; }
}

public sealed record RecordingStatus
{
    public required string RecordingId { get; init; }
    public string? ArtifactId { get; init; }
    public string State { get; init; } = "starting";
    public required string OutputPath { get; init; }
    public int ProcessId { get; init; }
    public int WorkerProcessId { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? StoppedAtUtc { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double ElapsedSeconds { get; init; }
    public long BytesWritten { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public string? EndReason { get; init; }
    public bool HardwareEncodingRequested { get; init; }
    public bool HardwareEncodingVerified { get; init; }
    public string? Encoder { get; init; }
    public string? Sha256 { get; init; }
    public long InputMarkersDropped { get; init; }
    public bool ManagedMemoryObserved { get; init; }
    public long ManagedHeapBytes { get; init; }
    public long ManagedAllocatedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public long? LohBytesAfterLastCollection { get; init; }
    public long? LastGcIndex { get; init; }
    public bool TexturePoolObserved { get; init; }
    public int EncoderTexturePoolCapacity { get; init; }
    public int EncoderTexturePoolAllocated { get; init; }
    public int EncoderTexturePoolInUse { get; init; }
    public int EncoderTexturePoolPeakInUse { get; init; }
    public long EncoderTexturePoolReuses { get; init; }
    public long EncoderTexturePoolTimeouts { get; init; }
    public long EncoderTexturePoolBytes { get; init; }
    public string[] Limitations { get; init; } = [];
}

public sealed class RecordingException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
