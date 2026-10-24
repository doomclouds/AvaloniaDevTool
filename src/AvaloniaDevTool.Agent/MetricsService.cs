using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Captures bounded raw Meter measurements without accessing inspected UI objects.</summary>
internal sealed class MetricsService : IDisposable
{
    private const int NotificationBatchSize = 64;
    private readonly Action<string, object> _notify;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();
    private readonly Queue<MetricSampleDto> _history = new();
    private readonly Queue<MetricSampleDto> _pending = new();
    private readonly Dictionary<long, MetricInstrumentDto> _instruments = [];
    private readonly Dictionary<string, SeriesState> _series = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _notificationTimer;
    private MetricsStartRequest _settings = new();
    private Regex[] _meterFilters = [];
    private Regex[] _instrumentFilters = [];
    private MeterListener? _listener;
    private string? _captureId;
    private long _generation, _nextInstrument, _nextSeries, _sequence;
    private long _received, _droppedPending, _evictedHistory, _droppedSeries, _droppedInstruments;
    private long _truncatedTags, _invalidMeasurements, _observableErrors, _notificationErrors;
    private long _expiredHistory, _revision;
    private DateTimeOffset _captureStart, _windowEnd;
    private string? _lastError;
    private bool _running, _disposed, _polling;
    private AvaloniaMetricsSupportDto? _avaloniaSupport;

    public MetricsService(Action<string, object> notify, TimeProvider? timeProvider = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        _notify = notify;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pollTimer = new DispatcherTimer();
        _pollTimer.Tick += OnPollTick;
        _notificationTimer = new DispatcherTimer();
        _notificationTimer.Tick += OnNotificationTick;
    }

    public MetricsStatusDto Start(MetricsStartRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var meters = CompileFilters(request.MeterNames ?? ["Avalonia*", "System.Runtime"]);
        var instruments = CompileFilters(request.InstrumentNames ?? []);
        StopCore();
        var listener = new MeterListener();
        long generation;
        lock (_gate)
        {
            _settings = request with
            {
                MeterNames = (request.MeterNames ?? ["Avalonia*", "System.Runtime"]).ToArray(),
                InstrumentNames = (request.InstrumentNames ?? []).ToArray()
            };
            _meterFilters = meters;
            _instrumentFilters = instruments;
            _history.Clear();
            _pending.Clear();
            _instruments.Clear();
            _series.Clear();
            _captureId = Guid.NewGuid().ToString("N");
            _captureStart = _windowEnd = _timeProvider.GetUtcNow();
            generation = ++_generation;
            _nextInstrument = _nextSeries = _sequence = 0;
            _received = _droppedPending = _evictedHistory = _droppedSeries = _droppedInstruments = 0;
            _truncatedTags = _invalidMeasurements = _observableErrors = _notificationErrors = 0;
            _expiredHistory = _revision = 0;
            _lastError = null;
            _running = true;
            _listener = listener;
        }

        listener.InstrumentPublished = (instrument, source) => PublishInstrument(instrument, source, generation);
        listener.MeasurementsCompleted = (instrument, state) =>
        {
            if (state is not InstrumentState registration)
                return;
            lock (_gate)
            {
                registration.Active = false;
                if (registration.Generation == _generation && _instruments.TryGetValue(registration.Id, out var item))
                    _instruments[registration.Id] = item with { IsActive = false };
            }
        };
        listener.SetMeasurementEventCallback<byte>(Record);
        listener.SetMeasurementEventCallback<short>(Record);
        listener.SetMeasurementEventCallback<int>(Record);
        listener.SetMeasurementEventCallback<long>(Record);
        listener.SetMeasurementEventCallback<float>(Record);
        listener.SetMeasurementEventCallback<double>(Record);
        listener.SetMeasurementEventCallback<decimal>(Record);
        try
        {
            listener.Start();
            _avaloniaSupport = Avalonia12MetricsAdapter.EnsureInitialized();
            _pollTimer.Interval = TimeSpan.FromMilliseconds(request.PollIntervalMs);
            _notificationTimer.Interval = TimeSpan.FromMilliseconds(request.NotificationIntervalMs);
            _pollTimer.Start();
            _notificationTimer.Start();
        }
        catch
        {
            StopCore();
            throw;
        }
        return Status();
    }

    public MetricsStatusDto Stop()
    {
        Dispatcher.UIThread.VerifyAccess();
        StopCore();
        return Status();
    }

    private void StopCore()
    {
        _pollTimer.Stop();
        _notificationTimer.Stop();
        MeterListener? listener;
        lock (_gate)
        {
            AdvanceWindowLocked();
            _running = false;
            _revision++;
            _pending.Clear();
            listener = _listener;
            _listener = null;
        }
        // MeterListener.Dispose may race an in-flight measurement callback.
        // The running/generation guard prevents that callback from committing data.
        listener?.Dispose();
    }

    public MetricsStatusDto Status()
    {
        lock (_gate)
        {
            AdvanceWindowLocked();
            return StatusLocked();
        }
    }

    public MetricsStatusDto Clear(MetricsClearRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            if (request.SeriesId is { } id && !_series.Values.Any(series => series.Id == id))
                throw new RpcException("invalid_request", "The metric series does not belong to this capture.");
            AdvanceWindowLocked();
            RemoveSeriesSamples(_history, request.SeriesId);
            RemoveSeriesSamples(_pending, request.SeriesId);
            _revision++;
            return StatusLocked();
        }
    }

    public MetricsSnapshotDto Snapshot()
    {
        MetricSampleDto[] samples;
        MetricInstrumentDto[] instruments;
        SeriesState[] series;
        MetricsStatusDto status;
        DateTimeOffset captureStart;
        lock (_gate)
        {
            AdvanceWindowLocked();
            samples = _history.ToArray();
            instruments = _instruments.Values.OrderBy(item => item.Id).ToArray();
            series = _series.Values.OrderBy(item => item.Id).ToArray();
            status = StatusLocked();
            captureStart = _captureStart;
        }
        var grouped = samples.GroupBy(sample => sample.SeriesId).ToDictionary(group => group.Key, group => group.ToArray());
        var statistics = series.Select(item =>
        {
            var window = grouped.GetValueOrDefault(item.Id) ?? [];
            if (window.Length == 0)
                return new MetricSeriesDto(item.Id, item.InstrumentId, item.Tags, 0, null, null, null, null, null, null, null);
            var values = window.Select(sample => sample.Value).Order().ToArray();
            var mean = values.Sum(value => value / values.Length);
            return new MetricSeriesDto(item.Id, item.InstrumentId, item.Tags, values.Length, values[0], values[^1],
                double.IsFinite(mean) ? mean : null, NearestRank(values, 0.5), NearestRank(values, 0.9),
                NearestRank(values, 0.95), window[^1].Value);
        }).ToArray();
        // Capture-relative frames stay aligned across series and successive snapshots.
        var frames = samples.GroupBy(sample => (sample.SeriesId,
                Bucket: status.AggregateFrames
                    ? (sample.Timestamp - captureStart).Ticks / TimeSpan.FromMilliseconds(status.FrameIntervalMs).Ticks
                    : sample.Sequence))
            .Select(group =>
            {
                var values = group.Select(sample => sample.Value).Order().ToArray();
                var last = group.MaxBy(sample => sample.Sequence)!;
                var kind = instruments.First(item => item.Id == last.InstrumentId).ValueSemantics;
                var mean = Finite(values.Sum(value => value / values.Length));
                var aggregation = !status.AggregateFrames ? "raw" : kind == "Observation" ? "observation_mean" : "last";
                var value = status.AggregateFrames && kind == "Observation" ? mean : last.Value;
                var start = status.AggregateFrames
                    ? captureStart.AddMilliseconds(group.Key.Bucket * (long)status.FrameIntervalMs) : last.Timestamp;
                return new MetricFrameDto(group.Key.SeriesId, start,
                    status.AggregateFrames ? start.AddMilliseconds(status.FrameIntervalMs) : start,
                    values.Length, values[0], values[^1], mean, NearestRank(values, .5), NearestRank(values, .9),
                    NearestRank(values, .95), value, aggregation);
            }).OrderBy(frame => frame.Start).ThenBy(frame => frame.SeriesId).ToArray();
        return new MetricsSnapshotDto(status, instruments, statistics, samples, frames);
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;

    private static void RemoveSeriesSamples(Queue<MetricSampleDto> queue, long? seriesId)
    {
        if (seriesId is null) { queue.Clear(); return; }
        var retained = queue.Where(sample => sample.SeriesId != seriesId).ToArray();
        queue.Clear();
        foreach (var sample in retained) queue.Enqueue(sample);
    }

    private void AdvanceWindowLocked()
    {
        if (!_running) return;
        var now = _timeProvider.GetUtcNow();
        if (now > _windowEnd) _windowEnd = now;
        var cutoff = _windowEnd.AddMilliseconds(-_settings.HistoryWindowMs);
        while (_history.TryPeek(out var sample) && sample.Timestamp < cutoff)
        {
            _history.Dequeue();
            _expiredHistory++;
            _revision++;
        }
        while (_pending.TryPeek(out var sample) && sample.Timestamp < cutoff)
        {
            _pending.Dequeue();
            _droppedPending++;
        }
    }

    private void PublishInstrument(Instrument instrument, MeterListener listener, long generation)
    {
        lock (_gate)
        {
            if (!_running || generation != _generation ||
                !Matches(_meterFilters, instrument.Meter.Name) || !Matches(_instrumentFilters, instrument.Name))
                return;
            if (_instruments.Count >= _settings.MaxInstruments)
            {
                _droppedInstruments++;
                return;
            }
            var kind = instrument.GetType().Name.Split('`')[0];
            var id = ++_nextInstrument;
            _instruments.Add(id, new MetricInstrumentDto(id, Limit(instrument.Meter.Name, 256),
                LimitNullable(instrument.Meter.Version, 64), Limit(instrument.Name, 256),
                LimitNullable(instrument.Description, 256), LimitNullable(instrument.Unit, 64), kind,
                instrument.IsObservable, Semantics(kind), true));
            listener.EnableMeasurementEvents(instrument, new InstrumentState(id, generation));
        }
    }

    private void Record<T>(Instrument instrument, T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) where T : struct
    {
        if (state is not InstrumentState registration)
            return;
        lock (_gate)
        {
            if (!_running || registration.Generation != _generation || !registration.Active)
                return;
            AdvanceWindowLocked();
            _received++;
            _revision++;
            var value = Convert.ToDouble(measurement, CultureInfo.InvariantCulture);
            if (!double.IsFinite(value))
            {
                _invalidMeasurements++;
                return;
            }
            var selectedTags = ReadTags(tags, _settings.MaxTags, out var truncated);
            if (truncated)
                _truncatedTags++;
            var key = SeriesKey(registration.Id, selectedTags);
            if (!_series.TryGetValue(key, out var series))
            {
                if (_series.Count >= _settings.MaxSeries)
                {
                    _droppedSeries++;
                    return;
                }
                series = new SeriesState(++_nextSeries, registration.Id, selectedTags);
                _series.Add(key, series);
            }
            var raw = measurement is IFormattable formattable
                ? formattable.ToString(measurement is double or float ? "R" : null, CultureInfo.InvariantCulture)
                : measurement.ToString() ?? "";
            var sample = new MetricSampleDto(++_sequence, series.Id, registration.Id, _windowEnd,
                value, typeof(T).Name, Limit(raw, 64), series.Tags);
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

    private void OnPollTick(object? sender, EventArgs args)
    {
        if (_disposed || _polling)
            return;
        MeterListener? listener;
        long generation;
        lock (_gate)
        {
            listener = _running ? _listener : null;
            generation = _generation;
        }
        if (listener is null)
            return;
        _polling = true;
        try { listener.RecordObservableInstruments(); }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (_running && generation == _generation)
                {
                    _observableErrors++;
                    _lastError = Limit(exception.Message, 256);
                }
            }
        }
        finally { _polling = false; }
    }

    private void OnNotificationTick(object? sender, EventArgs args)
    {
        if (_disposed)
            return;
        MetricsSamplesAddedNotification notification;
        lock (_gate)
        {
            AdvanceWindowLocked();
            if (!_running || _pending.Count == 0)
                return;
            var batch = new List<MetricSampleDto>(Math.Min(_pending.Count, NotificationBatchSize));
            while (batch.Count < NotificationBatchSize && _pending.TryDequeue(out var sample))
                batch.Add(sample);
            var ids = batch.Select(sample => sample.InstrumentId).Distinct();
            var instruments = ids.Select(id => _instruments[id]).ToArray();
            notification = new MetricsSamplesAddedNotification(batch, instruments, StatusLocked());
        }
        try { _notify(MetricsMethods.SampleAdded, notification); }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _notificationErrors++;
                _lastError = Limit(exception.Message, 256);
            }
        }
    }

    private MetricsStatusDto StatusLocked() => new(_running, _captureId,
        _settings.Capacity, _settings.PendingCapacity, _settings.MaxSeries, _settings.MaxInstruments, _settings.MaxTags,
        _settings.PollIntervalMs, _settings.NotificationIntervalMs,
        _instruments.Count, _series.Count, _history.Count, _pending.Count,
        _received, _droppedPending, _evictedHistory, _droppedSeries, _droppedInstruments, _truncatedTags,
        _invalidMeasurements, _observableErrors, _notificationErrors, _lastError,
        _settings.MeterNames, _settings.InstrumentNames, _avaloniaSupport,
        _settings.HistoryWindowMs, _settings.FrameIntervalMs,
        _captureId is null ? null : _windowEnd.AddMilliseconds(-_settings.HistoryWindowMs),
        _captureId is null ? null : _windowEnd, _expiredHistory, _revision, _sequence, _settings.AggregateFrames);

    private static MetricTagDto[] ReadTags(ReadOnlySpan<KeyValuePair<string, object?>> tags, int maximum, out bool truncated)
    {
        truncated = tags.Length > maximum;
        if (maximum == 0 || tags.Length == 0)
            return [];
        // Examine at most 64 entries; unusual high-cardinality tag sets cannot allocate unbounded copies.
        var selected = new List<MetricTagDto>(Math.Min(tags.Length, 64));
        for (var index = 0; index < Math.Min(tags.Length, 64); index++)
        {
            var pair = tags[index];
            var key = pair.Key ?? "";
            var value = TagValue(pair.Value);
            truncated |= key.Length > 48 || value?.Length > 96;
            selected.Add(new MetricTagDto(Limit(key, 48), LimitNullable(value, 96)));
        }
        return selected.OrderBy(tag => tag.Key, StringComparer.Ordinal).ThenBy(tag => tag.Value, StringComparer.Ordinal)
            .Take(maximum).ToArray();
    }

    private static string? TagValue(object? value) => value switch
    {
        null => null,
        string text => text,
        bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToString(value, CultureInfo.InvariantCulture),
        Guid guid => guid.ToString("D"),
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => "<" + Limit(value.GetType().FullName ?? value.GetType().Name, 92) + ">"
    };

    private static string SeriesKey(long instrumentId, IReadOnlyList<MetricTagDto> tags)
    {
        var key = new StringBuilder(instrumentId.ToString(CultureInfo.InvariantCulture));
        foreach (var tag in tags)
            key.Append('|').Append(tag.Key.Length).Append(':').Append(tag.Key)
                .Append('=').Append(tag.Value?.Length ?? -1).Append(':').Append(tag.Value);
        return key.ToString();
    }

    private static string Semantics(string kind) => kind switch
    {
        "Counter" or "UpDownCounter" => "Delta",
        "ObservableCounter" or "ObservableUpDownCounter" => "ReportedCumulative",
        "Histogram" => "Observation",
        "Gauge" or "ObservableGauge" => "CurrentValue",
        _ => "RawMeasurement"
    };

    private static double NearestRank(double[] values, double percentile) =>
        values[Math.Max(0, (int)Math.Ceiling(values.Length * percentile) - 1)];

    private static Regex[] CompileFilters(IReadOnlyList<string> patterns)
    {
        if (patterns.Count > 16 || patterns.Any(pattern => string.IsNullOrWhiteSpace(pattern) || pattern.Length > 128))
            throw new RpcException("invalid_request", "Provide at most 16 nonempty name patterns of at most 128 characters.");
        return patterns.Select(pattern => new Regex("\\A" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "\\z",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.Singleline)).ToArray();
    }

    private static bool Matches(Regex[] filters, string name) => filters.Length == 0 || filters.Any(filter => filter.IsMatch(name));
    private static string Limit(string text, int maximum) => text.Length <= maximum ? text : text[..maximum];
    private static string? LimitNullable(string? text, int maximum) => text is null ? null : Limit(text, maximum);

    private static void Validate(MetricsStartRequest request)
    {
        if (request.Capacity is < 1 or > 1024 || request.PendingCapacity is < 1 or > 2048 ||
            request.MaxSeries is < 1 or > 128 || request.MaxInstruments is < 1 or > 128 ||
            request.MaxTags is < 0 or > 8 || request.PollIntervalMs is < 100 or > 60000 ||
            request.NotificationIntervalMs is < 50 or > 5000 ||
            request.HistoryWindowMs is < 100 or > 600000 || request.FrameIntervalMs is < 10 or > 60000 ||
            request.FrameIntervalMs > request.HistoryWindowMs)
            throw new RpcException("invalid_request", "Metrics capacities or timing values are outside the supported limits.");
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        StopCore();
        _disposed = true;
        _pollTimer.Tick -= OnPollTick;
        _notificationTimer.Tick -= OnNotificationTick;
    }

    private sealed class InstrumentState(long id, long generation)
    {
        public long Id { get; } = id;
        public long Generation { get; } = generation;
        public bool Active { get; set; } = true;
    }

    private sealed record SeriesState(long Id, long InstrumentId, MetricTagDto[] Tags);
}

