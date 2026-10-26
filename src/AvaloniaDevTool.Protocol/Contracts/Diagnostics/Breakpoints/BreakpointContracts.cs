using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Protocol;

public static class BreakpointMethods
{
    public const string Create = "breakpoint.create";
    public const string Update = "breakpoint.update";
    public const string Remove = "breakpoint.remove";
    public const string List = "breakpoint.list";
    public const string History = "breakpoint.history";
    public const string Hit = "breakpoint.hit";
    public const string Changed = "breakpoint.changed";
}

[JsonConverter(typeof(JsonStringEnumConverter<BreakpointKind>))]
public enum BreakpointKind
{
    Property,
    Event
}

[JsonConverter(typeof(JsonStringEnumConverter<BreakpointComparison>))]
public enum BreakpointComparison
{
    Always,
    LessThan,
    LessThanOrEqual,
    Equal,
    GreaterThan,
    GreaterThanOrEqual
}

[JsonConverter(typeof(JsonStringEnumConverter<BreakpointState>))]
public enum BreakpointState
{
    Active,
    Disabled,
    ObjectExpired
}

public sealed record BreakpointSpec(
    BreakpointKind Kind,
    string MemberId,
    long? ObjectId = null,
    bool Enabled = true,
    BreakpointComparison Comparison = BreakpointComparison.Always,
    long CompareTo = 1,
    bool LogMessage = true,
    bool RemoveOnHit = false,
    bool Suspend = false,
    long? SourceObjectId = null);

public sealed record CreateBreakpointRequest(BreakpointSpec Definition);

public sealed record UpdateBreakpointRequest(long Id, BreakpointSpec Definition, bool ResetHitCount = false);

public sealed record RemoveBreakpointRequest(long Id);

public sealed record BreakpointInfo(long Id, BreakpointSpec Definition, long HitCount, BreakpointState State);

public sealed record BreakpointHit(
    long Sequence,
    long BreakpointId,
    long HitCount,
    long? ObjectId,
    string MemberId,
    string Summary,
    DateTimeOffset Timestamp,
    bool ConditionMet,
    bool SuspendSkipped = false);

public sealed record BreakpointHistory(IReadOnlyList<BreakpointHit> Hits, long DroppedCount);

public sealed record BreakpointChangedNotification(IReadOnlyList<BreakpointInfo> Breakpoints);
