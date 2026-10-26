using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Captures only bounded primitive descriptions; activity callbacks never touch the object registry.</summary>
internal sealed class ProfilerService : IDisposable
{
    private const int NotificationBatchSize = 64;
    [ThreadStatic] private static bool s_inCallback;
    private readonly object _gate = new();
    private readonly Action<string, object> _notify;
    private readonly DispatcherTimer _notificationTimer;
    private readonly Queue<ProfilerSampleDto> _history = new();
    private readonly Queue<ProfilerSampleDto> _pending = new();
    private readonly Dictionary<GroupKey, GroupState> _groups = [];
    private readonly HashSet<ActivityKey> _inFlight = [];
    private ProfilerStartRequest _settings = new();
    private ActivityListener? _listener;
    private string? _captureId, _lastError;
    private long _generation, _sequence, _nextGroup;
    private long _started, _completed, _filtered, _evictedHistory, _droppedPending, _droppedGroups;
    private long _droppedInFlight, _incomplete, _truncatedTags, _callbackErrors, _notificationErrors;
    private bool _running, _disposed;

    public ProfilerService(Action<string, object> notify)
    {
        Dispatcher.UIThread.VerifyAccess();
        _notify = notify;
        _notificationTimer = new DispatcherTimer();
        _notificationTimer.Tick += OnNotificationTick;
    }

    public ProfilerStatusDto Start(ProfilerStartRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var settings = request with
        {
            Categories = (request.Categories ?? Enum.GetValues<ProfilerCategory>()).Distinct().ToArray()
        };
        StopCore();
        long generation;
        lock (_gate)
        {
            _settings = settings;
            ResetLocked();
            _captureId = Guid.NewGuid().ToString("N");
            generation = ++_generation;
            _running = true;
        }

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == Avalonia12ActivityAdapter.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => Sampling(options.Name, generation),
            SampleUsingParentId = (ref ActivityCreationOptions<string> options) => Sampling(options.Name, generation),
            ActivityStarted = activity => OnStarted(activity, generation),
            ActivityStopped = activity => OnStopped(activity, generation)
        };
        lock (_gate)
            _listener = listener;
        try
        {
            ActivitySource.AddActivityListener(listener);
            Avalonia12ActivityAdapter.EnsureInitialized();
            _notificationTimer.Interval = TimeSpan.FromMilliseconds(settings.NotificationIntervalMs);
            _notificationTimer.Start();
        }
        catch
        {
            StopCore();
            throw;
        }

        return Status();
    }

    public ProfilerStatusDto Stop()
    {
        Dispatcher.UIThread.VerifyAccess();
        StopCore();
        return Status();
    }

    public ProfilerStatusDto Clear()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        bool running;
        ProfilerStartRequest settings;
        lock (_gate)
        {
            running = _running;
            settings = _settings;
        }

        // A fresh listener generation prevents old in-flight spans from appearing after Clear.
        if (running)
            return Start(settings);
        lock (_gate)
            ResetLocked();
        return Status();
    }

    private void StopCore()
    {
        _notificationTimer.Stop();
        ActivityListener? listener;
        lock (_gate)
        {
            _running = false;
            ++_generation;
            _incomplete += _inFlight.Count;
            _inFlight.Clear();
            _droppedPending += _pending.Count;
            _pending.Clear();
            listener = _listener;
            _listener = null;
        }

        // ActivityListener.Dispose can be followed by already-dispatched callbacks.
        listener?.Dispose();
    }

    public ProfilerStatusDto Status()
    {
        lock (_gate)
            return StatusLocked();
    }

    public ProfilerSnapshotDto Snapshot()
    {
        lock (_gate)
        {
            var samples = _history.ToArray();
            var grouped = samples.GroupBy(sample => sample.GroupId)
                .ToDictionary(group => group.Key, group => group.ToArray());
            var groups = _groups.Values.OrderBy(group => group.Id).Select(group =>
            {
                var window = grouped.GetValueOrDefault(group.Id) ?? [];
                var total = window.Sum(sample => sample.DurationMicroseconds);
                return new ProfilerGroupDto(group.Id,
                    group.Category,
                    group.Name,
                    window.Length,
                    total,
                    window.Length == 0 ? 0 : window.Min(sample => sample.DurationMicroseconds),
                    window.Length == 0 ? 0 : window.Max(sample => sample.DurationMicroseconds),
                    window.Length == 0 ? 0 : total / window.Length,
                    window.Count(sample => sample.IsMatch is true),
                    window.Count(sample => sample.IsMatch is false),
                    window.Count(sample => sample.IsMatch is null),
                    window.Count(sample => sample.FastReject));
            }).ToArray();
            return new ProfilerSnapshotDto(StatusLocked(), groups, samples);
        }
    }

    private ActivitySamplingResult Sampling(string name, long generation)
    {
        lock (_gate)
            return !s_inCallback && _running && generation == _generation && Category(name) is { } category &&
                   _settings.Categories!.Contains(category)
                ? ActivitySamplingResult.AllDataAndRecorded
                : ActivitySamplingResult.None;
    }

    private void OnStarted(Activity activity, long generation)
    {
        if (s_inCallback)
            return;
        s_inCallback = true;
        try
        {
            lock (_gate)
            {
                if (!_running || generation != _generation || Category(activity.OperationName) is not { } category ||
                    !_settings.Categories!.Contains(category))
                    return;
                _started++;
                if (_inFlight.Count >= _settings.MaxInFlight)
                {
                    _droppedInFlight++;
                    return;
                }

                _inFlight.Add(Key(activity));
            }
        }
        catch (Exception error)
        {
            ReportCallbackError(error, generation);
        }
        finally
        {
            s_inCallback = false;
        }
    }

    private void OnStopped(Activity activity, long generation)
    {
        if (s_inCallback)
            return;
        s_inCallback = true;
        try
        {
            lock (_gate)
            {
                if (!_running || generation != _generation || !_inFlight.Remove(Key(activity)) ||
                    Category(activity.OperationName) is not { } category)
                    return;
                _completed++;
                var tags = ReadTags(activity, _settings.MaxTags, out var values, out var identities, out var truncated);
                if (truncated)
                    _truncatedTags++;
                var style = category == ProfilerCategory.ResourceLookup ? null : values.GetValueOrDefault("Style");
                var selector = category == ProfilerCategory.ResourceLookup
                    ? null
                    : values.GetValueOrDefault("Selector") ?? (category == ProfilerCategory.StyleAttach ? style : null);
                var key = category == ProfilerCategory.ResourceLookup ? values.GetValueOrDefault("Key") : null;
                var theme = category == ProfilerCategory.ResourceLookup
                    ? values.GetValueOrDefault("ThemeVariant")
                    : null;
                if (_settings.Filter is { Length: > 0 } filter &&
                    !(selector?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) &&
                    !(key?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    _filtered++;
                    return;
                }

                var name = category == ProfilerCategory.ResourceLookup
                    ? (key ?? "<unknown key>") + " [theme=" + (theme ?? "<unspecified>") + "]"
                    : selector ?? style ?? "<unknown style>";
                var groupKey = category == ProfilerCategory.ResourceLookup
                    ? new GroupKey(category,
                        identities.GetValueOrDefault("Key") ?? "null",
                        identities.GetValueOrDefault("ThemeVariant") ?? "null")
                    : new GroupKey(category,
                        identities.GetValueOrDefault("Style") ?? name,
                        identities.GetValueOrDefault("Selector"));
                if (!_groups.TryGetValue(groupKey, out var group))
                {
                    if (_groups.Count >= _settings.MaxGroups)
                    {
                        _droppedGroups++;
                        return;
                    }

                    group = new GroupState(++_nextGroup, category, name);
                    _groups.Add(groupKey, group);
                }

                var result = values.GetValueOrDefault(category == ProfilerCategory.StyleAttach ? "SelectorResult" :
                    category == ProfilerCategory.StyleEvaluate ? "IsActive" : "Result");
                var active = category == ProfilerCategory.StyleEvaluate ? ParseBool(result) : null;
                bool? match = category switch
                {
                    ProfilerCategory.StyleAttach => result switch
                    {
                        "AlwaysThisInstance" or "AlwaysThisType" or "Sometimes" => true,
                        "NeverThisType" or "NeverThisInstance" => false,
                        _ => null
                    },
                    ProfilerCategory.StyleEvaluate => active,
                    _ => ParseBool(result)
                };
                var sample = new ProfilerSampleDto(++_sequence,
                    group.Id,
                    category,
                    activity.OperationName,
                    new DateTimeOffset(DateTime.SpecifyKind(activity.StartTimeUtc, DateTimeKind.Utc)),
                    Math.Max(0, activity.Duration.TotalMicroseconds),
                    style,
                    selector,
                    key,
                    theme,
                    result,
                    active,
                    match,
                    category == ProfilerCategory.StyleAttach && result == "NeverThisType",
                    tags);
                if (_history.Count >= _settings.Capacity)
                {
                    _history.Dequeue();
                    _evictedHistory++;
                }

                _history.Enqueue(sample);
                if (_pending.Count >= _settings.PendingCapacity)
                {
                    _pending.Dequeue();
                    _droppedPending++;
                }

                _pending.Enqueue(sample);
            }
        }
        catch (Exception error)
        {
            ReportCallbackError(error, generation);
        }
        finally
        {
            s_inCallback = false;
        }
    }

    private static ProfilerTagDto[] ReadTags(Activity activity,
        int maximum,
        out Dictionary<string, string?> values,
        out Dictionary<string, string> identities,
        out bool truncated)
    {
        values = new Dictionary<string, string?>(StringComparer.Ordinal);
        identities = new Dictionary<string, string>(StringComparer.Ordinal);
        var tags = new List<ProfilerTagDto>(maximum);
        truncated = false;
        var examined = 0;
        var textBudget = 192;
        foreach (var pair in activity.TagObjects)
        {
            if (examined++ >= 64)
            {
                truncated = true;
                break;
            }

            var formatted = Avalonia12ActivityAdapter.Text(pair.Value);
            var value = formatted is null ? null : Avalonia12ActivityAdapter.Limit(formatted, 96);
            var key = Avalonia12ActivityAdapter.Limit(pair.Key, 32);
            truncated |= pair.Key.Length > 32 || formatted?.Length > 96 || pair.Value is string { Length: > 256 };
            if (pair.Key is "Style" or "Selector" or "Key" or "ThemeVariant" or "SelectorResult" or "IsActive"
                or "Result")
            {
                values[pair.Key] = value;
                if (pair.Key is "Style" or "Selector" or "Key" or "ThemeVariant")
                    identities[pair.Key] = GroupIdentity(pair.Value);
            }

            if (tags.Count < maximum && textBudget >= key.Length)
            {
                var tagValue = value is null ? null : Avalonia12ActivityAdapter.Limit(value, textBudget - key.Length);
                truncated |= tagValue?.Length < value?.Length;
                tags.Add(new ProfilerTagDto(key, tagValue));
                textBudget -= key.Length + (tagValue?.Length ?? 0);
            }
            else
                truncated = true;
        }

        return tags.ToArray();
    }

    private void ReportCallbackError(Exception error, long generation)
    {
        lock (_gate)
            if (_running && generation == _generation)
            {
                _callbackErrors++;
                _lastError = Avalonia12ActivityAdapter.Limit(error.GetType().Name, 256);
            }
    }

    private void OnNotificationTick(object? sender, EventArgs args)
    {
        ProfilerSamplesAddedNotification notification;
        long generation;
        lock (_gate)
        {
            if (_disposed || !_running || _pending.Count == 0)
                return;
            generation = _generation;
            var samples = new List<ProfilerSampleDto>(Math.Min(NotificationBatchSize, _pending.Count));
            while (samples.Count < NotificationBatchSize && _pending.TryDequeue(out var sample))
                samples.Add(sample);
            notification = new ProfilerSamplesAddedNotification(samples, StatusLocked());
        }

        try
        {
            _notify(ProfilerMethods.SamplesAdded, notification);
        }
        catch (Exception error)
        {
            lock (_gate)
                if (_running && generation == _generation)
                {
                    _notificationErrors++;
                    _lastError = Avalonia12ActivityAdapter.Limit(error.GetType().Name, 256);
                }
        }
    }

    private ProfilerStatusDto StatusLocked() => new(_running,
        _captureId,
        _settings.Capacity,
        _settings.PendingCapacity,
        _settings.MaxGroups,
        _settings.MaxInFlight,
        _settings.MaxTags,
        _settings.NotificationIntervalMs,
        _groups.Count,
        _history.Count,
        _pending.Count,
        _inFlight.Count,
        _started,
        _completed,
        _filtered,
        _evictedHistory,
        _droppedPending,
        _droppedGroups,
        _droppedInFlight,
        _incomplete,
        _truncatedTags,
        _callbackErrors,
        _notificationErrors,
        _lastError,
        _settings.Categories,
        _settings.Filter);

    private void ResetLocked()
    {
        _history.Clear();
        _pending.Clear();
        _groups.Clear();
        _inFlight.Clear();
        _sequence = _nextGroup =
            _started = _completed = _filtered = _evictedHistory = _droppedPending = _droppedGroups = 0;
        _droppedInFlight = _incomplete = _truncatedTags = _callbackErrors = _notificationErrors = 0;
        _lastError = null;
    }

    private static ProfilerCategory? Category(string name) => name switch
    {
        "Avalonia.AttachingStyle" => ProfilerCategory.StyleAttach,
        "Avalonia.EvaluatingStyle" => ProfilerCategory.StyleEvaluate,
        "Avalonia.FindingResource" => ProfilerCategory.ResourceLookup,
        _ => null
    };

    private static bool? ParseBool(string? value) => value switch { "True" => true, "False" => false, _ => null };

    private static string GroupIdentity(object? value, int depth = 0)
    {
        if (value is null)
            return "null";
        if (value is string text)
            return "string:" + TextHash(text);
        if (value is Avalonia.Styling.ThemeVariant theme && depth < 4)
            return "theme:" + GroupIdentity(theme.Key, depth + 1);
        var type = value.GetType();
        var typeId = TextHash(type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
        if (value is bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double
            or decimal or
            Guid or DateTime or DateTimeOffset)
            return typeId + ":" + Avalonia12ActivityAdapter.Text(value);
        if (value is Type runtimeType && type.Assembly == typeof(Type).Assembly)
            return "type:" + TextHash(runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name);
        if (value is Enum enumeration)
            return typeId + ":" + Enum.Format(type, enumeration, "D");
        // Only a session-local digest is retained for object-valued keys/styles, never the application object.
        return typeId + ":object:" + RuntimeHelpers.GetHashCode(value)
            .ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string TextHash(string text) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(text.AsSpan())));

    private static ActivityKey Key(Activity activity)
    {
        if (activity.IdFormat == ActivityIdFormat.W3C)
            return new ActivityKey(activity.TraceId, activity.SpanId, null);
        // Hierarchical activities have default TraceId/SpanId. Hash their textual ID without retaining it.
        var id = activity.Id ?? "";
        return new ActivityKey(default, default, TextHash(id));
    }

    private static void Validate(ProfilerStartRequest request)
    {
        if (request.Capacity is < 1 or > 1024 || request.PendingCapacity is < 1 or > 2048 ||
            request.MaxGroups is < 1 or > 128 || request.MaxInFlight is < 1 or > 2048 ||
            request.MaxTags is < 0 or > 8 || request.NotificationIntervalMs is < 50 or > 5000 ||
            request.Filter?.Length > 128 || request.Categories is { Count: 0 or > 3 } ||
            request.Categories?.Any(category => !Enum.IsDefined(category)) is true)
            throw new RpcException("invalid_request",
                "Profiler capacities, categories, filter, or notification interval are outside the supported limits.");
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        StopCore();
        _disposed = true;
        _notificationTimer.Tick -= OnNotificationTick;
    }

    private sealed record GroupState(long Id, ProfilerCategory Category, string Name);

    private readonly record struct GroupKey(ProfilerCategory Category, string Identity, string? ThemeIdentity);

    private readonly record struct ActivityKey(ActivityTraceId Trace, ActivitySpanId Span, string? HierarchicalHash);
}
