namespace AvaloniaDevTool.Protocol;

public static partial class Methods
{
    public const string SessionInfo = "session.info";
}

public static class DiagnosticsProtocol
{
    public const int Version = 1;
    public const string PipePrefix = "avalonia-devtool-";
}

public sealed record SessionInfo(
    string SessionId,
    string ApplicationName,
    int ProcessId,
    string AvaloniaVersion,
    int ProtocolVersion,
    long ContentGeneration = 0);

public sealed record ObjectRequest(long ObjectId);

public sealed record OperationResult(bool Success = true);
