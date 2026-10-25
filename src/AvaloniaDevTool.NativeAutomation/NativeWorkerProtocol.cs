using System.Text.Json;

namespace AvaloniaDevTool.NativeAutomation;

// Shared only by the facade and worker; independent from the application's RPC protocol.
public sealed record NativeWorkerRequest(long Id, string Command, JsonElement Payload);
public sealed record NativeWorkerResponse(long Id, bool Success, JsonElement? Result = null,
    string? ErrorCode = null, string? ErrorMessage = null);
public sealed record NativeWorkerAttach(NativeTargetIdentity Target, string SessionId,
    int ParentProcessId = 0, DateTimeOffset? ParentStartTimeUtc = null);
