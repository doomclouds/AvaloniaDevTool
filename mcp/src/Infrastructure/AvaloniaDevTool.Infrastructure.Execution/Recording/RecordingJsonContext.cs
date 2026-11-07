using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Recording;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RecordingWorkerRequest))]
[JsonSerializable(typeof(RecordingWorkerResponse))]
[JsonSerializable(typeof(RecordingWorkerStart))]
[JsonSerializable(typeof(RecordingStatus))]
[JsonSerializable(typeof(RecordingStatus[]))]
[JsonSerializable(typeof(RecordingInputMarker))]
[JsonSerializable(typeof(RecordingPipeCommand<RecordingInputMarker>), TypeInfoPropertyName = "MarkerCommand")]
[JsonSerializable(typeof(RecordingPipeCommand<RecordingWorkerStart>), TypeInfoPropertyName = "StartCommand")]
[JsonSerializable(typeof(RecordingPipeCommand<JsonElement>), TypeInfoPropertyName = "ControlCommand")]
[JsonSerializable(typeof(RecordingPipeResponse))]
[JsonSerializable(typeof(RecordingOwner))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class RecordingJsonContext : JsonSerializerContext;

internal sealed record RecordingOwner(int ProcessId, DateTimeOffset StartTimeUtc);

// Same JSON shape as the worker envelope, with a typed payload/result to avoid an intermediate DOM.
internal readonly record struct RecordingPipeCommand<T>(long Id, string Command, T Payload);

internal sealed record RecordingPipeResponse(long Id, bool Success, RecordingStatus? Result = null,
    string? ErrorCode = null, string? ErrorMessage = null);
