using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Cli;

public sealed record AgentCaptureTarget(
    string ConnectionId,
    SessionInfo Session,
    string OwnershipScope = "same_client_connection");

public sealed record AgentActivitySnapshot(
    ActivityStatus Status,
    IReadOnlyList<ActivityEntry> Entries,
    ActivityStatus BaselineStatus,
    long AfterSequence);

public sealed record AgentCaptureIssue(string Service, string Code, string Message);

public sealed record AgentCaptureCompleteness(
    bool IsComplete,
    bool HasDataLoss,
    bool HasErrors,
    bool Frozen,
    string Scope = "retained_samples");

public sealed record AgentCaptureResult(
    string CaptureId,
    AgentCaptureTarget Target,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt,
    MetricsSnapshotDto? Metrics,
    ProfilerSnapshotDto? Profiler,
    AgentActivitySnapshot? Activity,
    AgentCaptureCompleteness Completeness,
    IReadOnlyList<AgentCaptureIssue> Issues);
