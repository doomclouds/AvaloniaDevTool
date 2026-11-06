using AvaloniaDevTool.Protocol;
using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Cli;

public sealed record ApplicationDescriptor(long Id, string Name, string PipeName, int? ProcessId);

public sealed record ApplicationAttachmentResult(
    bool Attached,
    SessionInfo? Session,
    IReadOnlyList<ApplicationDescriptor> Applications,
    bool Truncated = false,
    string? Guidance = null);

public sealed record AgentDragWaypoint(long NodeId, double X, double Y);

public sealed record UiWaitResult(bool Matched, string Condition, long? NodeId, string? PropertyValue, long ElapsedMs);

[JsonConverter(typeof(JsonStringEnumConverter<ElementControlAction>))]
public enum ElementControlAction
{
    Focus,
    Enable,
    Disable,
    BringIntoView
}

[JsonConverter(typeof(ControlInputEventJsonConverter))]
public enum ControlInputEvent
{
    Click,
    KeyDown,
    KeyUp,
    Text
}

public sealed class ControlInputEventJsonConverter()
    : JsonStringEnumConverter<ControlInputEvent>(allowIntegerValues: false);

public sealed record ResourceInspectionResult(
    long NodeId,
    bool OnlySelf,
    ResourceTreeSnapshot Tree,
    IReadOnlyList<ResourceQueryEntryInfo> Entries,
    bool Truncated,
    IReadOnlyList<string> Limitations);
