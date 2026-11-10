using System.Text.Json;
using AvaloniaDevTool.Recording;
using AvaloniaDevTool.Recording.Worker;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
using var output = Console.OpenStandardOutput();
byte[] newline = [10];
BackendIntegrity.Verify();
await using var recording = new WindowRecorder();
try
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        RecordingWorkerRequest? request = null;
        WorkerPipeResponse response;
        try
        {
            if (line.Length > 64 * 1024)
                throw new RecordingException("invalid_request", "Recording worker requests are limited to 64 KiB.");
            request = JsonSerializer.Deserialize<RecordingWorkerRequest>(line, json)
                ?? throw new RecordingException("invalid_request", "The worker request is empty.");
            var result = request.Command switch
            {
                "start" => recording.Start(request.Payload.Deserialize<RecordingWorkerStart>(json)
                    ?? throw new RecordingException("invalid_request", "Recording options are missing.")),
                "status" => recording.Snapshot(),
                "stop" => recording.Stop("requested"),
                "marker" => recording.Mark(request.Payload.Deserialize<RecordingInputMarker>(json)
                    ?? throw new RecordingException("invalid_request", "Input marker is missing.")),
                _ => throw new RecordingException("unknown_command", "Unknown recording worker command.")
            };
            response = new WorkerPipeResponse(request.Id, true, result);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            response = new WorkerPipeResponse(request?.Id ?? 0, false,
                ErrorCode: error is RecordingException recordingError ? recordingError.Code : "recording_worker_error",
                ErrorMessage: error.Message);
        }
        await JsonSerializer.SerializeAsync(output, response, json);
        await output.WriteAsync(newline);
        await output.FlushAsync();
    }
}
finally
{
    if (recording.IsStarted) recording.Stop("control_disconnected");
    await recording.WaitForCompletionAsync(TimeSpan.FromSeconds(10));
}

internal readonly record struct WorkerPipeResponse(long Id, bool Success, RecordingStatus? Result = null,
    string? ErrorCode = null, string? ErrorMessage = null);
