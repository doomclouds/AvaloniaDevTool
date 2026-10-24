using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Interactivity;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class ActivityService
{
    internal const int EventRouteCapacity = 200;
    internal const int EventRouteByteBudget = 2 * 1024 * 1024;
    internal const int MaximumRouteNodes = 128;
    internal const int ActiveRouteTtlSeconds = 30;
    internal const int EventRouteHistoryTtlSeconds = 300;
    private static readonly string[] DefaultEventGroups = ["Pointer", "Keyboard", "Focus", "Control", "Gesture", "DragDrop"];
    private readonly ConditionalWeakTable<RoutedEventArgs, RouteCorrelation> _eventCorrelations = new();
    private readonly Dictionary<long, RouteCapture> _eventRoutes = [];
    private readonly HashSet<long> _dirtyEventRoutes = [];
    private readonly TimeProvider _eventTimeProvider;
    private EventSelection? _eventSelection;
    private long _nextRouteId;
    private long _routeEpoch;
    private long _eventSubscriptionVersion;
    private long _eventRouteBytes;
    private long _eventRouteDropped;
    private long _eventRouteTruncated;
    private bool _eventRouteStatusDirty;

    public EventCatalog GetEventCatalog()
    {
        VerifyAccess();
        if (_running && _captureEvents) RefreshEventSubscriptions();
        return new(RoutedEventRegistry.Instance.GetAllRegistered().Select(item => new EventCatalogEntry(
            EventId(item), LogSnapshots.Limit(item.Name, 256), LogSnapshots.Limit(item.OwnerType.FullName ?? item.OwnerType.Name, 512),
            LogSnapshots.Limit(item.EventArgsType.FullName ?? item.EventArgsType.Name, 512), item.RoutingStrategies.ToString(), EventGroup(item)))
            .OrderBy(item => item.Group).ThenBy(item => item.OwnerType).ThenBy(item => item.Name).ToArray(), DefaultEventGroups);
    }

    public IReadOnlyList<EventRouteInfo> EventRoutesSnapshot()
    {
        VerifyAccess();
        ExpireEventRoutes();
        PublishDirtyEventRoutes();
        return _eventRoutes.Values.OrderBy(item => item.Info.RouteId).Select(item => WithExpiry(item.Info)).ToArray();
    }

    public EventRouteStatus EventRoutesStatus()
    {
        VerifyAccess();
        ExpireEventRoutes();
        return new(_eventRoutes.Count, _eventRouteBytes, EventRouteCapacity, EventRouteByteBudget,
            _eventRouteDropped, _eventRouteTruncated, MaximumRouteNodes, ActiveRouteTtlSeconds, EventRouteHistoryTtlSeconds);
    }

    private static void ValidateEventSelection(EventSelection? selection)
    {
        if (selection is null) return;
        if (selection.EventIds?.Count > 1024 || selection.EventIds?.Any(item => item is null || item.Length > 1024) == true ||
            selection.Groups?.Count > DefaultEventGroups.Length || selection.Groups?.Any(item => !DefaultEventGroups.Contains(item, StringComparer.Ordinal)) == true)
            throw new RpcException("invalid_request", "The routed event selection is invalid or exceeds its limit.");
    }

    private bool MatchesEvent(RoutedEvent routedEvent)
    {
        if (_eventSelection is { } selection)
            return selection.IncludeAll || selection.EventIds?.Contains(EventId(routedEvent), StringComparer.Ordinal) == true ||
                selection.Groups?.Contains(EventGroup(routedEvent), StringComparer.Ordinal) == true;
        if (_eventName is { } legacy)
            return legacy == "*" || routedEvent.Name.Contains(legacy, StringComparison.OrdinalIgnoreCase) ||
                $"{routedEvent.OwnerType.Name}.{routedEvent.Name}".Contains(legacy, StringComparison.OrdinalIgnoreCase);
        return DefaultEventGroups.Contains(EventGroup(routedEvent), StringComparer.Ordinal);
    }

    private static string EventId(RoutedEvent item)
    {
        var identity = $"{item.OwnerType.Assembly.GetName().Name}:{item.OwnerType.FullName}.{item.Name}";
        return identity.Length <= 1024 ? identity : "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)));
    }

    private static string EventGroup(RoutedEvent item)
    {
        var name = item.Name;
        if (name.StartsWith("Pointer", StringComparison.Ordinal)) return "Pointer";
        if (name.StartsWith("Key", StringComparison.Ordinal) || name.StartsWith("TextInput", StringComparison.Ordinal)) return "Keyboard";
        if (name.Contains("Focus", StringComparison.Ordinal)) return "Focus";
        if (name.StartsWith("Drag", StringComparison.Ordinal) || name == "Drop") return "DragDrop";
        if (item.OwnerType.Name.Contains("Gesture", StringComparison.Ordinal) || name.Contains("Gesture", StringComparison.Ordinal) ||
            name is "Tapped" or "DoubleTapped" or "Holding") return "Gesture";
        return "Control";
    }

    private long? ObserveEventRoute(RoutedEvent routedEvent, object? sender, RoutedEventArgs args, bool finished)
    {
        var correlation = _eventCorrelations.GetOrCreateValue(args);
        if (correlation.Active && correlation.Epoch != _routeEpoch)
        {
            if (finished && IsFinalStrategy(routedEvent, args.Route)) FinishCorrelation(correlation);
            return null;
        }
        if (!finished && correlation.Active && _eventRoutes.TryGetValue(correlation.Id, out var active) &&
            active.FirstStrategy == args.Route && sender is not null && active.FirstReceiverId == _objects.GetId(sender))
        {
            // Reusing the same args recursively starts another raise. The holder contains only IDs.
            correlation.Parent = new RouteCorrelation { Id = correlation.Id, Epoch = correlation.Epoch, Active = true, Parent = correlation.Parent };
            correlation.Active = false;
        }
        if (!correlation.Active)
        {
            // RouteFinished alone can be observed after a clear or a subscription made mid-route.
            if (finished) return null;
            correlation.Active = true;
            correlation.Epoch = _routeEpoch;
            correlation.Id = ++_nextRouteId;
            var source = args.Source ?? sender;
            var info = new EventRouteInfo(correlation.Id, EventId(routedEvent), LogSnapshots.Limit(routedEvent.Name, 256),
                LogSnapshots.Limit(routedEvent.OwnerType.FullName ?? routedEvent.OwnerType.Name, 512), _eventTimeProvider.GetUtcNow(), null,
                source is null ? null : _objects.GetId(source), SafeType(source), SafeName(source), [], [], false, null, "Active");
            _eventRoutes.Add(info.RouteId, new RouteCapture(info, args.Route, sender is null ? null : _objects.GetId(sender)));
        }
        var id = correlation.Id;
        if (!_eventRoutes.TryGetValue(id, out var capture))
        {
            if (finished && IsFinalStrategy(routedEvent, args.Route)) FinishCorrelation(correlation);
            return null;
        }
        var current = capture.Info;
        if (current.State != "Active")
        {
            if (finished && IsFinalStrategy(routedEvent, args.Route)) FinishCorrelation(correlation);
            return null;
        }
        if (finished)
        {
            var final = IsFinalStrategy(routedEvent, args.Route);
            current = current with
            {
                FinishedStrategies = current.FinishedStrategies.Append(args.Route.ToString()).Distinct().ToArray(),
                PhaseFinishes = (current.PhaseFinishes ?? []).Append(new(args.Route.ToString(), args.Handled,
                    capture.LastHandled is { } last && last != args.Handled)).Take(3).ToArray(),
                IsFinished = final, FinalHandled = args.Handled,
                FinishedAt = final ? _eventTimeProvider.GetUtcNow() : null, State = final ? "Finished" : "Active"
            };
            if (final) FinishCorrelation(correlation);
        }
        else if (current.Nodes.Count < MaximumRouteNodes)
        {
            var nodes = current.Nodes.Append(new EventRouteNode(current.Nodes.Count, args.Route.ToString(),
                sender is null ? null : _objects.GetId(sender), SafeType(sender), SafeName(sender), args.Handled,
                capture.LastHandled is { } previous && previous != args.Handled)).ToArray();
            current = current with { Nodes = nodes };
        }
        else if (!current.IsTruncated)
        {
            current = current with { IsTruncated = true };
            _eventRouteTruncated++;
        }
        capture.LastHandled = args.Handled;
        capture.Info = current;
        RecalculateEventRouteBytes(capture);
        _dirtyEventRoutes.Add(id);
        _eventRouteStatusDirty = true;
        EnforceEventRouteBudget();
        return id;
    }

    private static bool IsFinalStrategy(RoutedEvent routedEvent, RoutingStrategies route) =>
        route == RoutingStrategies.Direct || route == RoutingStrategies.Bubble ||
        route == RoutingStrategies.Tunnel && (routedEvent.RoutingStrategies & RoutingStrategies.Bubble) == 0;

    private static void FinishCorrelation(RouteCorrelation holder)
    {
        if (holder.Parent is not { } parent) { holder.Active = false; return; }
        holder.Id = parent.Id;
        holder.Epoch = parent.Epoch;
        holder.Active = parent.Active;
        holder.Parent = parent.Parent;
    }

    private static string SafeType(object? value) => LogSnapshots.Limit(value?.GetType().FullName ?? "Unknown", 512);
    private static string? SafeName(object? value) => value is StyledElement element ? LogSnapshots.Limit(element.Name, 256) : null;

    private EventRouteInfo WithExpiry(EventRouteInfo info) => info with
    {
        SourceExpired = info.SourceObjectId is { } sourceId && !_objects.TryGet(sourceId, out _),
        Nodes = info.Nodes.Select(node => node with
        { ReceiverExpired = node.ReceiverObjectId is { } id && !_objects.TryGet(id, out _) }).ToArray()
    };

    private void EnforceEventRouteBudget()
    {
        while (_eventRoutes.Count > EventRouteCapacity || _eventRouteBytes > EventRouteByteBudget)
        {
            var oldest = _eventRoutes.Keys.Min();
            RemoveEventRoute(oldest);
            _eventRouteDropped++;
        }
    }

    private void RemoveEventRoute(long id)
    {
        if (_eventRoutes.Remove(id, out var removed)) _eventRouteBytes -= removed.ByteCost;
        _dirtyEventRoutes.Remove(id);
        _eventRouteStatusDirty = true;
    }

    private void RecalculateEventRouteBytes(RouteCapture capture)
    {
        _eventRouteBytes -= capture.ByteCost;
        capture.ByteCost = ProtocolJson.SerializeToUtf8Bytes(capture.Info).Length + 64;
        _eventRouteBytes += capture.ByteCost;
    }

    private void ExpireEventRoutes()
    {
        var now = _eventTimeProvider.GetUtcNow();
        foreach (var capture in _eventRoutes.Values.ToArray())
        {
            if ((now - capture.Info.StartedAt).TotalSeconds > EventRouteHistoryTtlSeconds)
            { RemoveEventRoute(capture.Info.RouteId); _eventRouteDropped++; }
            else if (capture.Info.State == "Active" && (now - capture.Info.StartedAt).TotalSeconds > ActiveRouteTtlSeconds)
            {
                if (!capture.Info.IsTruncated) _eventRouteTruncated++;
                capture.Info = capture.Info with { State = "Expired", IsTruncated = true };
                RecalculateEventRouteBytes(capture);
                _dirtyEventRoutes.Add(capture.Info.RouteId);
            }
        }
        EnforceEventRouteBudget();
    }

    private void PublishDirtyEventRoutes()
    {
        ExpireEventRoutes();
        if (_dirtyEventRoutes.Count > 0) _eventRouteStatusDirty = true;
        foreach (var id in _dirtyEventRoutes.ToArray())
            if (_eventRoutes.TryGetValue(id, out var capture)) SafeNotify(EventRouteMethods.RouteUpdated, WithExpiry(capture.Info));
        _dirtyEventRoutes.Clear();
        if (_eventRouteStatusDirty)
        {
            _eventRouteStatusDirty = false;
            SafeNotify(EventRouteMethods.StateChanged, EventRoutesStatus());
        }
    }

    private void ClearEventRoutes()
    {
        _routeEpoch++;
        _eventRoutes.Clear();
        _dirtyEventRoutes.Clear();
        _eventRouteBytes = _eventRouteDropped = _eventRouteTruncated = 0;
        _eventRouteStatusDirty = true;
    }

    private void CancelActiveEventRoutes()
    {
        _routeEpoch++;
        foreach (var capture in _eventRoutes.Values.Where(item => item.Info.State == "Active"))
        {
            if (!capture.Info.IsTruncated) _eventRouteTruncated++;
            capture.Info = capture.Info with { State = "Stopped", IsTruncated = true };
            RecalculateEventRouteBytes(capture);
            _dirtyEventRoutes.Add(capture.Info.RouteId);
        }
        EnforceEventRouteBudget();
    }

    private sealed class RouteCorrelation
    {
        public long Id;
        public long Epoch;
        public bool Active;
        public RouteCorrelation? Parent;
    }

    private sealed class RouteCapture(EventRouteInfo info, RoutingStrategies firstStrategy, long? firstReceiverId)
    {
        public EventRouteInfo Info = info;
        public int ByteCost;
        public bool? LastHandled;
        public RoutingStrategies FirstStrategy { get; } = firstStrategy;
        public long? FirstReceiverId { get; } = firstReceiverId;
    }
}
