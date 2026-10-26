using System.Text.Json;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Captures bounded application logs and routed-event observations for one inspector session.</summary>
internal sealed partial class ActivityService : IDisposable
{
    internal const int Capacity = 500;
    internal const int BufferedByteBudget = 2 * 1024 * 1024;
    internal const int MaximumEntryBytes = 128 * 1024;
    internal const int NotificationByteBudget = 512 * 1024;
    internal const int NotificationCapacity = 100;
    private const int BatchSize = 100;
    private readonly WeakObjectRegistry _objects;
    private readonly Action<string, object> _notify;
    private readonly Func<string, object, CancellationToken, Task>? _asyncNotify;
    private readonly object _queueLock = new();
    private readonly Queue<PendingActivity> _pending = new();
    private readonly Queue<BufferedActivity> _history = new();
    private readonly Queue<PendingNotification> _notifications = new();
    private readonly Dictionary<RoutedEvent, EventSubscriptions> _eventSubscriptions = [];
    private readonly DispatcherTimer _timer;
    private readonly IReadOnlyList<IDiagnosticsLogSource> _logSources;
    private readonly List<IDisposable> _logSubscriptions = [];
    private LogFilter? _logFilter;
    [ThreadStatic] private static bool _capturingLog;
    private CaptureSink? _sink;
    private volatile bool _running;
    private volatile bool _disposed;
    private volatile bool _captureLogs;
    private bool _captureEvents;
    private volatile LogEventLevel _minimumLevel = LogEventLevel.Warning;
    private string? _eventName;
    private long? _objectId;
    private long _nextSequence;
    private long _droppedCount;
    private long _byteEvictedCount;
    private long _budgetTruncatedCount;
    private long _pendingBytes;
    private long _historyBytes;
    private bool _statusDirty;
    private CancellationTokenSource _notificationCancellation = new();
    private long _notificationBytes;
    private long _notificationDroppedCount;
    private long _notificationStatsEpoch;
    private int _inFlightNotificationBytes;
    private bool _notificationSending;

    public ActivityService(WeakObjectRegistry objects,
        Action<string, object> notify,
        IReadOnlyList<IDiagnosticsLogSource>? logSources = null,
        Func<string, object, CancellationToken, Task>? asyncNotify = null,
        TimeProvider? timeProvider = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        _objects = objects;
        _notify = notify;
        _asyncNotify = asyncNotify;
        _eventTimeProvider = timeProvider ?? TimeProvider.System;
        _logSources = logSources ?? [];
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += OnTick;
    }

    public OperationResult Start(ActivityStartRequest request)
    {
        VerifyAccess();
        if (!request.CaptureLogs && !request.CaptureEvents)
            throw new RpcException("invalid_request", "Select at least one activity source.");
        if (!Enum.TryParse<LogEventLevel>(request.MinimumLogLevel, true, out var level) || !Enum.IsDefined(level))
            throw new RpcException("invalid_request", "The minimum log level is not recognized.");
        if (request.ObjectId is { } objectId)
            _ = _objects.Get(objectId);
        if (request.EventName?.Length > 256)
            throw new RpcException("invalid_request", "The event name exceeds 256 characters.");
        ValidateEventSelection(request.EventSelection);
        StopCore();
        _captureLogs = request.CaptureLogs;
        _captureEvents = request.CaptureEvents;
        _minimumLevel = level;
        _logFilter = request.LogFilter;
        _objectId = request.ObjectId;
        _eventName = string.IsNullOrWhiteSpace(request.EventName) ? null : request.EventName.Trim();
        _eventSelection = request.EventSelection;
        _running = true;
        if (_captureLogs)
        {
            _sink = new CaptureSink(this, Logger.Sink);
            Logger.Sink = _sink;
            foreach (var source in _logSources)
            {
                try
                {
                    _logSubscriptions.Add(source.Subscribe(CaptureCustomLog));
                }
                catch
                {
                    /* An unavailable custom source cannot disable framework capture. */
                }
            }
        }

        if (_captureEvents)
            RefreshEventSubscriptions();
        _timer.Start();
        PublishStatus();
        return new OperationResult();
    }

    public OperationResult Stop()
    {
        VerifyAccess();
        StopCore();
        PublishStatus();
        return new OperationResult();
    }

    public OperationResult Clear()
    {
        VerifyAccess();
        lock (_queueLock)
        {
            _pending.Clear();
            _history.Clear();
            _pendingBytes = _historyBytes = 0;
            _droppedCount = 0;
            _byteEvictedCount = 0;
            _budgetTruncatedCount = 0;
            _statusDirty = false;
        }

        ResetNotificationDelivery(clearCounters: true);
        ClearEventRoutes();
        PublishStatus();
        return new OperationResult();
    }

    public IReadOnlyList<ActivityEntry> Snapshot()
    {
        VerifyAccess();
        Flush(int.MaxValue);
        lock (_queueLock)
            return _history.Select(item => item.Entry).ToArray();
    }

    public ActivityStatus Status()
    {
        VerifyAccess();
        return CreateStatus();
    }

    private void StopCore()
    {
        _running = false;
        _eventSubscriptionVersion++;
        _timer.Stop();
        foreach (var subscription in _logSubscriptions)
        {
            try
            {
                subscription.Dispose();
            }
            catch
            {
            }
        }

        _logSubscriptions.Clear();
        if (_sink is { } sink)
        {
            sink.Deactivate();
            if (ReferenceEquals(Logger.Sink, sink))
                Logger.Sink = sink.RestoreTarget;
            _sink = null;
        }

        foreach (var subscription in _eventSubscriptions.Values)
            subscription.Dispose();
        _eventSubscriptions.Clear();
        CancelActiveEventRoutes();
        Flush(int.MaxValue);
        ResetNotificationDelivery();
    }

    private void RefreshEventSubscriptions()
    {
        var version = _eventSubscriptionVersion;
        foreach (var routedEvent in RoutedEventRegistry.Instance.GetAllRegistered().ToArray())
        {
            if (_eventSubscriptions.ContainsKey(routedEvent) || !MatchesEvent(routedEvent))
                continue;
            var route = routedEvent.AddClassHandler(typeof(Interactive),
                (sender, args) => OnEvent(routedEvent, sender, args, false, version),
                RoutingStrategies.Direct | RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                true);
            var finished = routedEvent.RouteFinished.Subscribe(
                new ActionObserver<RoutedEventArgs>(args => OnEvent(routedEvent, null, args, true, version)));
            _eventSubscriptions.Add(routedEvent, new EventSubscriptions(route, finished));
        }
    }

    private void OnEvent(RoutedEvent routedEvent, object? sender, RoutedEventArgs args, bool finished, long version)
    {
        if (!_running || !_captureEvents || version != _eventSubscriptionVersion || !Dispatcher.UIThread.CheckAccess())
            return;
        var source = args.Source ?? sender;
        if (_objectId is { } filter &&
            (!_objects.TryGet(filter, out var target) || !ReferenceEquals(target, source)))
            return;
        var routeId = ObserveEventRoute(routedEvent, sender, args, finished);
        if (routeId is null)
            return;
        var route = finished ? "Finished" : args.Route.ToString();
        var sourceType = source?.GetType().Name ?? "Unknown";
        var targetType = sender?.GetType().Name;
        var message = finished
            ? $"{routedEvent} from {sourceType}; route finished, handled={args.Handled}."
            : $"{routedEvent} from {sourceType} at {targetType ?? "Unknown"}; {route}, handled={args.Handled}.";
        Enqueue(new PendingActivity(0,
            DateTimeOffset.UtcNow,
            "Event",
            "Information",
            LogSnapshots.Limit(routedEvent.OwnerType.Name, 128),
            LogSnapshots.Limit(message, 4096),
            Weak(source),
            LogSnapshots.Limit(routedEvent.Name, 128),
            route,
            args.Handled,
            Weak(sender),
            EventRouteId: routeId));
    }

    private bool CaptureEnabled(LogEventLevel level) => _running && _captureLogs && level >= _minimumLevel;

    private void CaptureLog(LogEventLevel level, string area, object? source, string messageTemplate, object?[]? values)
    {
        if (!CaptureEnabled(level))
            return;
        CaptureCustomLog(new DiagnosticsLogEvent(level.ToString(),
            area,
            messageTemplate,
            LogSnapshots.AvaloniaParameters(messageTemplate, values),
            values?.Take(32).OfType<Exception>().FirstOrDefault(),
            Source: source,
            Origin: "Avalonia"));
    }

    private void CaptureCustomLog(DiagnosticsLogEvent entry)
    {
        if (_capturingLog || !Enum.TryParse<LogEventLevel>(entry.Level, true, out var level) ||
            !Enum.IsDefined(level) || !CaptureEnabled(level))
            return;
        _capturingLog = true;
        try
        {
            var details = LogSnapshots.Capture(entry, out var message);
            var category = LogSnapshots.Limit(entry.Category, 128);
            if (!LogSnapshots.Matches(_logFilter, category, message, details))
                return;
            Enqueue(new PendingActivity(0,
                DateTimeOffset.UtcNow,
                "Log",
                level.ToString(),
                category,
                message,
                Weak(entry.Source),
                null,
                null,
                null,
                null,
                details));
        }
        catch
        {
            /* Custom state implementations are application code; capture must fail closed. */
        }
        finally
        {
            _capturingLog = false;
        }
    }

    private void Enqueue(PendingActivity item)
    {
        // Measure the same UTF-8 JSON representation as the RPC, including worst-case numeric IDs.
        // The temporary DTO shares its immutable strings/lists with the queued item.
        var byteCost = ProtocolJson.SerializeToUtf8Bytes(ToEntry(item,
            long.MinValue,
            item.Source is null ? null : long.MinValue,
            item.Handler is null ? null : long.MinValue)).Length + 1;
        lock (_queueLock)
        {
            if (_disposed || !_running)
                return;
            if (byteCost > MaximumEntryBytes)
            {
                _droppedCount++;
                _byteEvictedCount++;
                _statusDirty = true;
                return;
            }

            while (_pending.Count + _history.Count >= Capacity ||
                   _pendingBytes + _historyBytes + byteCost > BufferedByteBudget)
            {
                var byteEviction = _pendingBytes + _historyBytes + byteCost > BufferedByteBudget;
                if (_history.TryDequeue(out var oldest))
                    _historyBytes -= oldest.ByteCost;
                else if (_pending.TryDequeue(out var pending))
                    _pendingBytes -= pending.ByteCost;
                else
                    break;
                _droppedCount++;
                if (byteEviction)
                    _byteEvictedCount++;
                _statusDirty = true;
            }

            if (item.LogDetails?.ByteBudgetTruncated == true)
                _budgetTruncatedCount++;
            _pending.Enqueue(item with { Sequence = ++_nextSequence, ByteCost = byteCost });
            _pendingBytes += byteCost;
        }
    }

    private static ActivityEntry ToEntry(PendingActivity item, long sequence, long? sourceId, long? handlerId) =>
        new(sequence,
            item.Timestamp,
            item.Kind,
            item.Level,
            item.Category,
            item.Message,
            sourceId,
            item.EventName,
            item.Route,
            item.Handled,
            handlerId,
            item.LogDetails,
            item.EventRouteId);

    private void OnTick(object? sender, EventArgs args)
    {
        if (_disposed || !_running)
            return;
        if (_captureEvents)
            RefreshEventSubscriptions();
        Flush(BatchSize);
    }

    private void Flush(int maximum)
    {
        Dispatcher.UIThread.VerifyAccess();
        lock (_queueLock)
            maximum = Math.Min(maximum, _pending.Count);
        var processed = 0;
        while (processed < maximum)
        {
            ActivityEntry entry;
            int byteCost;
            lock (_queueLock)
            {
                if (!_pending.TryDequeue(out var item))
                    break;
                _pendingBytes -= item.ByteCost;
                byteCost = item.ByteCost;
                // Registry identity resolution runs only on the UI thread and invokes no application callbacks.
                entry = ToEntry(item, item.Sequence, Resolve(item.Source), Resolve(item.Handler));
                _history.Enqueue(new BufferedActivity(entry, item.ByteCost));
                _historyBytes += item.ByteCost;
            }

            SafeNotify(ActivityMethods.EntryAdded, entry, byteCost);
            processed++;
        }

        bool statusDirty;
        lock (_queueLock)
        {
            statusDirty = _statusDirty;
            _statusDirty = false;
        }

        if (processed > 0 || statusDirty)
            PublishStatus();
        PublishDirtyEventRoutes();
    }

    private long? Resolve(WeakReference<object>? reference) =>
        reference is not null && reference.TryGetTarget(out var target) ? _objects.GetId(target) : null;

    private static WeakReference<object>? Weak(object? value) =>
        value is null ? null : new WeakReference<object>(value);

    private ActivityStatus CreateStatus()
    {
        lock (_queueLock)
            return new ActivityStatus(_running,
                _captureLogs,
                _captureEvents,
                _history.Count,
                _droppedCount,
                _minimumLevel.ToString(),
                _eventName,
                _objectId,
                _pendingBytes + _historyBytes,
                BufferedByteBudget,
                _byteEvictedCount,
                _budgetTruncatedCount,
                _notificationBytes,
                NotificationByteBudget,
                _notificationDroppedCount,
                _notifications.Count + (_inFlightNotificationBytes > 0 ? 1 : 0));
    }

    private void PublishStatus() => SafeNotify(ActivityMethods.StateChanged, CreateStatus());

    private void SafeNotify(string method, object payload, int? byteCost = null)
    {
        // Diagnostics observers must not break the application's logging or event route.
        var capturing = _capturingLog;
        _capturingLog = true;
        try
        {
            if (_asyncNotify is null)
                _notify(method, payload);
            else
                QueueNotification(method, payload, byteCost ?? ProtocolJson.SerializeToUtf8Bytes(payload).Length);
        }
        catch
        {
        }
        finally
        {
            _capturingLog = capturing;
        }
    }

    private void QueueNotification(string method, object payload, int byteCost)
    {
        const int envelopeReserve = 256;
        byteCost += envelopeReserve;
        var startPump = false;
        lock (_queueLock)
        {
            if (_disposed)
                return;
            while (_notifications.Count + (_inFlightNotificationBytes > 0 ? 1 : 0) >= NotificationCapacity ||
                   _notificationBytes + byteCost > NotificationByteBudget)
            {
                if (!_notifications.TryDequeue(out var oldest))
                {
                    _notificationDroppedCount++;
                    _statusDirty = true;
                    return;
                }

                _notificationBytes -= oldest.ByteCost;
                _notificationDroppedCount++;
                _statusDirty = true;
            }

            _notifications.Enqueue(new PendingNotification(method,
                payload,
                byteCost,
                _notificationCancellation.Token,
                _notificationStatsEpoch));
            _notificationBytes += byteCost;
            if (!_notificationSending)
            {
                _notificationSending = true;
                startPump = true;
            }
        }

        if (startPump)
            _ = PumpNotificationsAsync();
    }

    private async Task PumpNotificationsAsync()
    {
        while (true)
        {
            PendingNotification item;
            lock (_queueLock)
            {
                if (!_notifications.TryDequeue(out item!))
                {
                    _notificationSending = false;
                    return;
                }

                _inFlightNotificationBytes = item.ByteCost;
            }

            try
            {
                item.Cancellation.ThrowIfCancellationRequested();
                Task sending;
                var capturing = _capturingLog;
                _capturingLog = true;
                try
                {
                    sending = _asyncNotify!(item.Method, item.Payload, item.Cancellation);
                }
                finally
                {
                    _capturingLog = capturing;
                }

                // Await the actual sender completion. Cancellation cannot create a second in-flight sender.
                await sending.ConfigureAwait(false);
            }
            catch
            {
                lock (_queueLock)
                {
                    if (item.StatsEpoch == _notificationStatsEpoch)
                        _notificationDroppedCount++;
                    _statusDirty = true;
                }
            }
            finally
            {
                lock (_queueLock)
                {
                    _notificationBytes -= item.ByteCost;
                    _inFlightNotificationBytes = 0;
                }
            }
        }
    }

    private void ResetNotificationDelivery(bool clearCounters = false)
    {
        CancellationTokenSource previous;
        lock (_queueLock)
        {
            previous = _notificationCancellation;
            _notificationCancellation = new CancellationTokenSource();
            if (clearCounters)
            {
                _notificationStatsEpoch++;
                _notificationDroppedCount = 0;
            }
            else
                _notificationDroppedCount += _notifications.Count;

            _notifications.Clear();
            _notificationBytes = _inFlightNotificationBytes;
        }

        // Cancellation registrations and senders are external callbacks; never invoke them under the queue lock.
        try
        {
            previous.Cancel();
        }
        catch
        {
        }
        finally
        {
            previous.Dispose();
        }
    }

    private void VerifyAccess()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        StopCore();
        _disposed = true;
        _notificationCancellation.Dispose();
        _timer.Tick -= OnTick;
        lock (_queueLock)
        {
            _pending.Clear();
            _pendingBytes = 0;
        }
    }

    private sealed record PendingActivity(
        long Sequence,
        DateTimeOffset Timestamp,
        string Kind,
        string Level,
        string Category,
        string Message,
        WeakReference<object>? Source,
        string? EventName,
        string? Route,
        bool? Handled,
        WeakReference<object>? Handler,
        LogDetails? LogDetails = null,
        int ByteCost = 0,
        long? EventRouteId = null);

    private sealed record BufferedActivity(ActivityEntry Entry, int ByteCost);

    private sealed record PendingNotification(
        string Method,
        object Payload,
        int ByteCost,
        CancellationToken Cancellation,
        long StatsEpoch);

    private sealed record EventSubscriptions(IDisposable Route, IDisposable Finished) : IDisposable
    {
        public void Dispose()
        {
            Route.Dispose();
            Finished.Dispose();
        }
    }

    private sealed class ActionObserver<T>(Action<T> next) : IObserver<T>
    {
        public void OnNext(T value) => next(value);

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }

    private sealed class CaptureSink(ActivityService owner, ILogSink? previous) : ILogSink
    {
        [ThreadStatic] private static HashSet<CaptureSink>? _forwarding;
        private WeakReference<ActivityService>? _owner = new(owner);
        public ILogSink? Previous { get; } = previous;

        public ILogSink? RestoreTarget => Previous is CaptureSink capture && Volatile.Read(ref capture._owner) is null
            ? capture.RestoreTarget
            : Previous;

        public void Deactivate() => Volatile.Write(ref _owner, null);

        public bool IsEnabled(LogEventLevel level, string area) =>
            (Volatile.Read(ref _owner)?.TryGetTarget(out var capture) == true && capture.CaptureEnabled(level)) ||
            Previous?.IsEnabled(level, area) == true;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            Forward(level, area, source, messageTemplate, null);
        }

        public void Log(LogEventLevel level,
            string area,
            object? source,
            string messageTemplate,
            params object?[] propertyValues)
        {
            Forward(level, area, source, messageTemplate, propertyValues);
        }

        private void Forward(LogEventLevel level, string area, object? source, string template, object?[]? values)
        {
            if (!(_forwarding ??= []).Add(this))
                return;
            try
            {
                if (Previous?.IsEnabled(level, area) == true)
                {
                    if (values is null)
                        Previous.Log(level, area, source, template);
                    else
                        Previous.Log(level, area, source, template, values);
                }

                if (Volatile.Read(ref _owner)?.TryGetTarget(out var capture) == true)
                    capture.CaptureLog(level, area, source, template, values);
            }
            finally
            {
                _forwarding.Remove(this);
            }
        }
    }
}
