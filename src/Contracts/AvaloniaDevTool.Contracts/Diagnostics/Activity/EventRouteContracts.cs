namespace AvaloniaDevTool.Protocol;

public static class EventRouteMethods
{
    public const string Catalog = "events.catalog";
    public const string Snapshot = "events.routes";
    public const string Status = "events.status";
    public const string RouteUpdated = "events.routeUpdated";
    public const string StateChanged = "events.state";
}

/// <summary>Registry identities are exact selections; groups also include events registered later.</summary>
public sealed record EventSelection(
    IReadOnlyList<string>? EventIds = null,
    IReadOnlyList<string>? Groups = null,
    bool IncludeAll = false);

public sealed record EventCatalogEntry(
    string Id,
    string Name,
    string OwnerType,
    string EventArgsType,
    string RoutingStrategies,
    string Group);

public sealed record EventCatalog(IReadOnlyList<EventCatalogEntry> Events, IReadOnlyList<string> DefaultGroups);

/// <summary>One public Raised observation, before instance handlers on this receiver.</summary>
public sealed record EventRouteNode(
    int Index,
    string Strategy,
    long? ReceiverObjectId,
    string ReceiverType,
    string? ReceiverName,
    bool HandledObserved,
    bool HandledChangedSincePreviousObservation,
    string Observation = "RaisedClassHandler",
    bool ReceiverExpired = false);

public sealed record EventRoutePhaseFinished(
    string Strategy,
    bool HandledObserved,
    bool HandledChangedSincePreviousObservation);

public sealed record EventRouteInfo(
    long RouteId,
    string EventId,
    string EventName,
    string OwnerType,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    long? SourceObjectId,
    string SourceType,
    string? SourceName,
    IReadOnlyList<EventRouteNode> Nodes,
    IReadOnlyList<string> FinishedStrategies,
    bool IsFinished,
    bool? FinalHandled,
    string State,
    bool IsTruncated = false,
    bool SourceExpired = false,
    string HandledObservation =
        "Raised observes before instance handlers; changes are intervals, not handler attribution.",
    IReadOnlyList<EventRoutePhaseFinished>? PhaseFinishes = null);

public sealed record EventRouteStatus(
    int BufferedCount,
    long BufferedBytes,
    int Capacity,
    long ByteBudget,
    long DroppedCount,
    long TruncatedCount,
    int MaximumNodesPerRoute,
    int ActiveRouteTtlSeconds,
    int HistoryTtlSeconds);
