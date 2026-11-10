using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Owns reversible property/event observations and bounded hit history for one session.</summary>
[DebuggerNonUserCode]
internal sealed class BreakpointService : IDisposable
{
    internal const int Capacity = 200;
    private const int MaximumBreakpoints = 128;
    private const int NotificationBatchSize = 50;
    private readonly WeakObjectRegistry _objects;
    private readonly Action<string, object> _notify;
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly Queue<BreakpointHit> _history = new();
    private readonly Queue<BreakpointHit> _pendingNotifications = new();
    private readonly DispatcherTimer _flushTimer;
    private long _nextId;
    private long _sequence;
    private long _dropped;
    private int _pendingDeliveries;
    private volatile bool _disposed;
    [ThreadStatic] private static bool s_logging;

    public BreakpointService(WeakObjectRegistry objects, Action<string, object> notify)
    {
        Dispatcher.UIThread.VerifyAccess();
        _objects = objects;
        _notify = notify;
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _flushTimer.Tick += OnFlush;
    }

    public BreakpointInfo Create(CreateBreakpointRequest request)
    {
        VerifyAccess();
        if (_entries.Count >= MaximumBreakpoints)
            throw new RpcException("breakpoint_limit", "A session supports at most 128 breakpoints.");
        var entry = BuildEntry(++_nextId, request.Definition, 0);
        _entries.Add(entry.Id, entry);
        PublishChanged();
        return ToInfo(entry);
    }

    public BreakpointInfo Update(UpdateBreakpointRequest request)
    {
        VerifyAccess();
        var previous = GetEntry(request.Id);
        var replacement = BuildEntry(request.Id,
            request.Definition,
            request.ResetHitCount ? 0 : previous.HitCount,
            allowInactiveSuspend: true);
        previous.StopListening();
        _entries[request.Id] = replacement;
        PublishChanged();
        return ToInfo(replacement);
    }

    public OperationResult Remove(RemoveBreakpointRequest request)
    {
        VerifyAccess();
        var entry = GetEntry(request.Id);
        _entries.Remove(request.Id);
        entry.StopListening();
        PublishChanged();
        return new OperationResult();
    }

    public IReadOnlyList<BreakpointInfo> List()
    {
        VerifyAccess();
        return _entries.Values.OrderBy(entry => entry.Id).Select(ToInfo).ToArray();
    }

    public BreakpointHistory History()
    {
        VerifyAccess();
        return new BreakpointHistory(_history.ToArray(), Interlocked.Read(ref _dropped));
    }

    private Entry GetEntry(long id) => _entries.GetValueOrDefault(id) ??
                                       throw new RpcException("breakpoint_not_found",
                                           $"Breakpoint {id} does not exist.");

    private Entry BuildEntry(long id, BreakpointSpec definition, long hitCount, bool allowInactiveSuspend = false)
    {
        if (definition is null)
            throw new RpcException("invalid_request", "The breakpoint definition is required.");
        if (!Enum.IsDefined(definition.Kind) || !Enum.IsDefined(definition.Comparison) || definition.CompareTo < 0)
            throw new RpcException("invalid_request", "The breakpoint kind, comparison or hit threshold is invalid.");
        if (definition.Kind != BreakpointKind.Event && definition.SourceObjectId is not null)
            throw new RpcException("invalid_request", "SourceObjectId is only supported for event breakpoints.");
        var (ownerName, memberName) = ParseMember(definition.MemberId);
        if (definition.Suspend && !Debugger.IsAttached && (!allowInactiveSuspend || definition.Enabled))
            throw new RpcException("debugger_not_attached",
                "Attach a debugger before creating or enabling a suspend breakpoint.");
        var target = definition.ObjectId is { } objectId
            ? _objects.Get(objectId) as AvaloniaObject ??
              throw new RpcException("unsupported_object", "The breakpoint target must be an AvaloniaObject.")
            : null;
        var source = definition.SourceObjectId is { } sourceId
            ? _objects.Get(sourceId) as Interactive ??
              throw new RpcException("unsupported_object", "The event source must be Interactive.")
            : null;
        var entry = new Entry(id, definition, target, source) { HitCount = hitCount };
        if (definition.Kind == BreakpointKind.Property)
        {
            entry.Property = ResolveProperty(ownerName, memberName, target);
            if (definition.Enabled)
            {
                if (target is null)
                    entry.Subscription =
                        entry.Property.Changed.Subscribe(new PropertyObserver(args => OnProperty(entry, args)));
                else
                {
                    EventHandler<AvaloniaPropertyChangedEventArgs> handler = (_, args) => OnProperty(entry, args);
                    target.PropertyChanged += handler;
                    entry.Subscription = new ActionSubscription(() =>
                    {
                        if (entry.Target?.TryGetTarget(out var alive) == true)
                            alive.PropertyChanged -= handler;
                    });
                }
            }
        }
        else
        {
            if (target is not null && target is not Interactive)
                throw new RpcException("unsupported_object", "Event breakpoints require an Interactive target.");
            entry.Event = RoutedEventRegistry.Instance.GetAllRegistered().SingleOrDefault(routedEvent =>
                              routedEvent.OwnerType.FullName == ownerName && routedEvent.Name == memberName) ??
                          throw new RpcException("event_not_found",
                              "The owner-qualified routed event is not registered.");
            if (definition.Enabled)
            {
                var routes = RoutingStrategies.Direct | RoutingStrategies.Tunnel | RoutingStrategies.Bubble;
                var raised = entry.Event.AddClassHandler(typeof(Interactive),
                    (sender, args) => OnEvent(entry, sender, args),
                    routes,
                    true);
                var finished =
                    entry.Event.RouteFinished.Subscribe(
                        new ActionObserver<RoutedEventArgs>(args => OnEventFinished(entry, args)));
                entry.Subscription = new ActionSubscription(() =>
                {
                    raised.Dispose();
                    finished.Dispose();
                });
            }
        }

        return entry;
    }

    private static (string Owner, string Name) ParseMember(string memberId)
    {
        if (string.IsNullOrWhiteSpace(memberId) || memberId.Length > 2048)
            throw new RpcException("invalid_member",
                "Use the complete owner-qualified member ID: Namespace.Type:Member.");
        var separator = memberId.LastIndexOf(':');
        if (separator <= 0 || separator == memberId.Length - 1 || memberId.IndexOf(':') != separator)
            throw new RpcException("invalid_member",
                "Use the complete owner-qualified member ID: Namespace.Type:Member.");
        return (memberId[..separator], memberId[(separator + 1)..]);
    }

    private static AvaloniaProperty ResolveProperty(string ownerName, string memberName, AvaloniaObject? target)
    {
        var registry = AvaloniaPropertyRegistry.Instance;
        IEnumerable<AvaloniaProperty> candidates;
        if (target is not null)
            candidates = registry.GetRegistered(target.GetType())
                .Concat(registry.GetRegisteredAttached(target.GetType()));
        else
        {
            var owners = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(ownerName, throwOnError: false))
                .Where(type => type is not null).Distinct().ToArray();
            if (owners.Length != 1)
                throw new RpcException("property_not_found",
                    "The property owner must resolve uniquely among loaded assemblies.");
            var owner = owners[0]!;
            candidates = registry.GetRegistered(owner).Concat(registry.GetRegisteredDirect(owner))
                .Concat(registry.GetRegisteredAttached(owner))
                .Concat(registry.GetRegisteredAttached(typeof(Control)))
                .Concat(registry.GetRegisteredAttached(typeof(StyledElement)));
        }

        return candidates.Distinct().SingleOrDefault(property =>
                   property.OwnerType.FullName == ownerName && property.Name == memberName) ??
               throw new RpcException("property_not_found",
                   "The owner-qualified property is not registered for this target.");
    }

    [DebuggerHidden]
    private void OnProperty(Entry entry, AvaloniaPropertyChangedEventArgs args)
    {
        if (_disposed || entry.IsStopped || s_logging || args.Property != entry.Property)
            return;
        string value;
        try
        {
            value = PropertyValues.Format(args.NewValue) ?? "null";
        }
        catch (Exception exception)
        {
            value = $"<{exception.GetType().Name}>";
        }

        Deliver(entry, Weak(args.Sender), Limit("New value: " + value));
    }

    [DebuggerHidden]
    private void OnEvent(Entry entry, object? sender, RoutedEventArgs args)
    {
        if (_disposed || entry.IsStopped || s_logging)
            return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        if (entry.TargetExpired)
        {
            entry.IsExpired = true;
            entry.StopListening();
            PublishChanged();
            return;
        }

        if (!entry.PendingEvents.TryGetValue(args, out var pending))
        {
            if (entry.PendingEvents.Count >= Capacity)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            pending = new PendingEvent();
            entry.PendingEvents.Add(args, pending);
            // A throwing target handler may prevent RouteFinished. Bound that incomplete observation.
            pending.Cleanup = Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (entry.PendingEvents.Remove(args, out var incomplete))
                    {
                        entry.PendingObservationCount -= incomplete.Frames.Count;
                        Interlocked.Add(ref _dropped, incomplete.Frames.Count);
                    }
                },
                DispatcherPriority.Background);
        }

        if (pending.IsDiscarded)
            return;
        var frame = pending.Frames.LastOrDefault();
        if (frame is null || (args.Route == frame.FirstStrategy &&
                              (args.Route == RoutingStrategies.Direct ||
                               frame.FirstReceiver?.TryGetTarget(out var first) == true &&
                               ReferenceEquals(first, sender) ||
                               args.Route == RoutingStrategies.Tunnel && sender is Interactive root &&
                               root.GetVisualParent() is not Interactive ||
                               args.Route == RoutingStrategies.Bubble && ReferenceEquals(sender, args.Source))))
        {
            if (entry.PendingObservationCount >= Capacity)
            {
                // Do not attribute an overflowing recursive raise to its parent frame.
                pending.IsDiscarded = true;
                Interlocked.Add(ref _dropped, pending.Frames.Count + 1);
                entry.PendingObservationCount -= pending.Frames.Count;
                pending.Frames.Clear();
                return;
            }

            var source = args.Source ?? sender;
            var matchesSource = entry.Source is null || entry.Source.TryGetTarget(out var requiredSource) &&
                ReferenceEquals(requiredSource, source);
            frame = new EventObservation(Weak(source), Weak(sender), args.Route, matchesSource);
            pending.Frames.Add(frame);
            entry.PendingObservationCount++;
        }

        if (entry.Target is null || entry.Target.TryGetTarget(out var receiver) && ReferenceEquals(receiver, sender))
            frame.ReceiverMatched = true;
    }

    [DebuggerHidden]
    private void OnEventFinished(Entry entry, RoutedEventArgs args)
    {
        if (_disposed || entry.IsStopped || s_logging || !Dispatcher.UIThread.CheckAccess() ||
            !entry.PendingEvents.TryGetValue(args, out var pending) || pending.IsDiscarded)
            return;
        var frame = pending.Frames[^1];
        // Nested RaiseEvent may mutate the shared args.Route. Count actual phase finishes instead.
        frame.FinishedPhases++;
        var strategies = entry.Event!.RoutingStrategies;
        var phaseCount = strategies != RoutingStrategies.Direct && (strategies & RoutingStrategies.Tunnel) != 0 &&
                         (strategies & RoutingStrategies.Bubble) != 0
            ? 2
            : 1;
        if (frame.FinishedPhases < phaseCount)
            return;
        pending.Frames.RemoveAt(pending.Frames.Count - 1);
        entry.PendingObservationCount--;
        if (pending.Frames.Count == 0)
        {
            entry.PendingEvents.Remove(args);
            pending.Cleanup?.Abort();
        }

        if (frame.SourceMatched && frame.ReceiverMatched)
            Deliver(entry,
                frame.Source,
                Limit($"{entry.Definition.MemberId}; route={args.Route}; handled={args.Handled}"));
    }

    [DebuggerHidden]
    private void Deliver(Entry entry, WeakReference<object>? source, string summary)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ProcessHit(entry, source, summary);
            return;
        }

        if (Interlocked.Increment(ref _pendingDeliveries) > Capacity)
        {
            Interlocked.Decrement(ref _pendingDeliveries);
            Interlocked.Increment(ref _dropped);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Decrement(ref _pendingDeliveries);
            ProcessHit(entry, source, summary);
        });
    }

    [DebuggerHidden]
    private void ProcessHit(Entry entry, WeakReference<object>? weakSource, string summary)
    {
        if (_disposed || entry.IsStopped || !_entries.TryGetValue(entry.Id, out var current) ||
            !ReferenceEquals(current, entry))
            return;
        if (entry.TargetExpired)
        {
            entry.IsExpired = true;
            entry.StopListening();
            PublishChanged();
            return;
        }

        entry.HitCount++;
        var condition = MatchesCondition(entry.Definition, entry.HitCount);
        object? source = null;
        weakSource?.TryGetTarget(out source);
        var sourceId = source is not null ? _objects.GetId(source) : (long?)null;
        var suspendSkipped = condition && entry.Definition.Suspend && !Debugger.IsAttached;
        var hit = new BreakpointHit(++_sequence,
            entry.Id,
            entry.HitCount,
            sourceId,
            entry.Definition.MemberId,
            summary,
            DateTimeOffset.UtcNow,
            condition,
            suspendSkipped);
        if (_history.Count == Capacity)
        {
            _history.Dequeue();
            Interlocked.Increment(ref _dropped);
        }

        _history.Enqueue(hit);
        if (_pendingNotifications.Count == Capacity)
            _pendingNotifications.Dequeue();
        _pendingNotifications.Enqueue(hit);
        if (!_flushTimer.IsEnabled)
            _flushTimer.Start();

        if (!condition)
            return;
        if (entry.Definition.RemoveOnHit)
        {
            _entries.Remove(entry.Id);
            entry.StopListening();
            PublishChanged();
        }

        if (entry.Definition.LogMessage)
            WriteLog(source, hit);
        if (entry.Definition.Suspend && !suspendSkipped)
            SuspendOnSourceThread(source as AvaloniaObject, entry);
    }

    private static bool MatchesCondition(BreakpointSpec definition, long hitCount) => definition.Comparison switch
    {
        BreakpointComparison.Always => true,
        BreakpointComparison.LessThan => hitCount < definition.CompareTo,
        BreakpointComparison.LessThanOrEqual => hitCount <= definition.CompareTo,
        BreakpointComparison.Equal => hitCount == definition.CompareTo,
        BreakpointComparison.GreaterThan => hitCount > definition.CompareTo,
        BreakpointComparison.GreaterThanOrEqual => hitCount >= definition.CompareTo,
        _ => false
    };

    private static void WriteLog(object? source, BreakpointHit hit)
    {
        if (s_logging)
            return;
        try
        {
            s_logging = true;
            if (Logger.Sink is { } sink && sink.IsEnabled(LogEventLevel.Information, "DevTool.Breakpoints"))
                sink.Log(LogEventLevel.Information,
                    "DevTool.Breakpoints",
                    source,
                    "Breakpoint {Id} matched {Member} on hit {Count}: {Summary}",
                    hit.BreakpointId,
                    hit.MemberId,
                    hit.HitCount,
                    hit.Summary);
        }
        catch (Exception)
        {
            /* A logging observer must not throw into the target application. */
        }
        finally
        {
            s_logging = false;
        }
    }

    [DebuggerHidden]
    [DebuggerStepThrough]
    [DebuggerNonUserCode]
    private void SuspendOnSourceThread(AvaloniaObject? source, Entry entry)
    {
        if (_disposed || !Debugger.IsAttached)
            return;
        if (source is not null && !source.CheckAccess())
        {
            var owner = new WeakReference<BreakpointService>(this);
            source.Dispatcher.Post(() =>
            {
                if (owner.TryGetTarget(out var service) && !service._disposed &&
                    (!entry.IsStopped || entry.Definition.RemoveOnHit))
                    BreakNow();
            });
        }
        else
            BreakNow();
    }

    [DebuggerHidden]
    [DebuggerStepThrough]
    [DebuggerNonUserCode]
    private static void BreakNow()
    {
        if (Debugger.IsAttached)
            Debugger.Break();
    }

    private BreakpointInfo ToInfo(Entry entry)
    {
        if (entry.TargetExpired)
        {
            entry.IsExpired = true;
            entry.StopListening();
        }

        return new BreakpointInfo(entry.Id,
            entry.Definition,
            entry.HitCount,
            entry.IsExpired ? BreakpointState.ObjectExpired :
            entry.Definition.Enabled ? BreakpointState.Active : BreakpointState.Disabled);
    }

    private void OnFlush(object? sender, EventArgs args)
    {
        for (var count = 0; count < NotificationBatchSize && _pendingNotifications.TryDequeue(out var hit); count++)
            SafeNotify(BreakpointMethods.Hit, hit);
        if (_pendingNotifications.Count == 0)
            _flushTimer.Stop();
    }

    private void PublishChanged() => SafeNotify(BreakpointMethods.Changed,
        new BreakpointChangedNotification(List()));

    private void SafeNotify(string method, object payload)
    {
        try
        {
            _notify(method, payload);
        }
        catch (Exception)
        {
            /* Session shutdown must not throw into property or event dispatch. */
        }
    }

    private static string Limit(string value) => value.Length <= 1024 ? value : value[..1024] + "…";

    private static WeakReference<object>? Weak(object? value) =>
        value is null ? null : new WeakReference<object>(value);

    private void VerifyAccess()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Dispatcher.UIThread.VerifyAccess();
        _disposed = true;
        _flushTimer.Stop();
        _flushTimer.Tick -= OnFlush;
        foreach (var entry in _entries.Values)
            entry.StopListening();
        _entries.Clear();
        _pendingNotifications.Clear();
        _history.Clear();
    }

    private sealed class Entry(long id, BreakpointSpec definition, AvaloniaObject? target, Interactive? source)
    {
        public long Id { get; } = id;
        public BreakpointSpec Definition { get; } = definition;
        public WeakReference<AvaloniaObject>? Target { get; } = target is null ? null : new(target);
        public WeakReference<Interactive>? Source { get; } = source is null ? null : new(source);

        public bool TargetExpired => Target is not null && !Target.TryGetTarget(out _) ||
                                     Source is not null && !Source.TryGetTarget(out _);

        public AvaloniaProperty? Property { get; set; }
        public RoutedEvent? Event { get; set; }
        public IDisposable? Subscription { get; set; }
        public long HitCount { get; set; }
        public bool IsExpired { get; set; }
        public volatile bool IsStopped;

        public Dictionary<RoutedEventArgs, PendingEvent> PendingEvents { get; } =
            new(ReferenceEqualityComparer.Instance);

        public int PendingObservationCount { get; set; }

        public void StopListening()
        {
            IsStopped = true;
            Subscription?.Dispose();
            Subscription = null;
            foreach (var pending in PendingEvents.Values)
                pending.Cleanup?.Abort();
            PendingEvents.Clear();
            PendingObservationCount = 0;
        }
    }

    private sealed class PendingEvent
    {
        public List<EventObservation> Frames { get; } = [];
        public DispatcherOperation? Cleanup { get; set; }
        public bool IsDiscarded { get; set; }
    }

    private sealed class EventObservation(
        WeakReference<object>? source,
        WeakReference<object>? firstReceiver,
        RoutingStrategies firstStrategy,
        bool sourceMatched)
    {
        public WeakReference<object>? Source { get; } = source;
        public WeakReference<object>? FirstReceiver { get; } = firstReceiver;
        public RoutingStrategies FirstStrategy { get; } = firstStrategy;
        public bool SourceMatched { get; } = sourceMatched;
        public bool ReceiverMatched { get; set; }
        public int FinishedPhases { get; set; }
    }

    private sealed class ActionSubscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;
        public void Dispose() => Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }

    private sealed class PropertyObserver(Action<AvaloniaPropertyChangedEventArgs> callback)
        : IObserver<AvaloniaPropertyChangedEventArgs>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(AvaloniaPropertyChangedEventArgs value) => callback(value);
    }

    private sealed class ActionObserver<T>(Action<T> callback) : IObserver<T>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value) => callback(value);
    }
}
