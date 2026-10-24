using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private readonly HashSet<long> _metricSampleSequences = [];
    private DispatcherTimer? _metricsSnapshotTimer;
    private MetricsStatusDto? _metricsStatus;
    private bool _metricsRefreshing;
    private long _metricsSnapshotSequence;
    private long _metricsOperationVersion, _metricsRefreshId;
    private bool _metricsMutationPending;
    private int _metricsPollIntervalMs = 1000, _metricsNotificationIntervalMs = 200;
    private int _metricsFrameIntervalMs = 250, _metricsHistoryWindowMs = 60000;
    private bool _metricsAggregateFrames = true;
    private IReadOnlyList<MetricFrameDto> _metricFrames = [];
    private string _meterFilter = "Avalonia*,System.Runtime,AvaloniaDevTool.Sample";
    private string _instrumentFilter = "*";
    private MetricSeriesRowViewModel? _selectedMetricSeries;
    private IReadOnlyList<double> _metricChartValues = [];
    public ObservableCollection<MetricInstrumentDto> MetricInstruments { get; } = [];
    public ObservableCollection<MetricSeriesRowViewModel> MetricSeries { get; } = [];
    public ObservableCollection<MetricSampleRowViewModel> MetricSamples { get; } = [];
    public ObservableCollection<MetricChartRowViewModel> MetricCharts { get; } = [];
    public AsyncCommand StartMetricsCommand { get; private set; } = null!;
    public AsyncCommand StopMetricsCommand { get; private set; } = null!;
    public AsyncCommand ClearMetricSeriesCommand { get; private set; } = null!;
    public MetricsStatusDto? CurrentMetricsStatus => _metricsStatus;
    public string MeterFilter { get => _meterFilter; set => Set(ref _meterFilter, value ?? ""); }
    public string InstrumentFilter { get => _instrumentFilter; set => Set(ref _instrumentFilter, value ?? ""); }
    public int MetricsPollIntervalMs { get => _metricsPollIntervalMs; set => Set(ref _metricsPollIntervalMs, value); }
    public int MetricsNotificationIntervalMs { get => _metricsNotificationIntervalMs; set => Set(ref _metricsNotificationIntervalMs, value); }
    public int MetricsFrameIntervalMs { get => _metricsFrameIntervalMs; set => Set(ref _metricsFrameIntervalMs, value); }
    public int MetricsHistoryWindowMs { get => _metricsHistoryWindowMs; set => Set(ref _metricsHistoryWindowMs, value); }
    public bool MetricsAggregateFrames { get => _metricsAggregateFrames; set => Set(ref _metricsAggregateFrames, value); }
    public string MetricsSummary => _metricsStatus is { } status
        ? $"{(status.IsRunning ? "正在采集" : "已停止")} · {status.InstrumentCount} 个仪表 · {status.SeriesCount} 个序列 · 保留 {status.BufferedSampleCount}/{status.Capacity} 条样本\n" +
          $"时间窗 {status.HistoryWindowMs} ms · 轮询 {status.PollIntervalMs} ms · 通知 {status.NotificationIntervalMs} ms · 显示帧 {status.FrameIntervalMs} ms · {(status.AggregateFrames ? "帧聚合" : "全部原始点")}\n" +
          $"通知丢弃 {status.DroppedPendingSamples} · 容量淘汰 {status.EvictedHistorySamples} · 时间过期 {status.ExpiredHistorySamples} · 序列限制丢弃 {status.DroppedSeriesSamples} · 仪表限制丢弃 {status.DroppedInstruments} · 标签截断 {status.TruncatedTagSets} · 无效值 {status.InvalidMeasurements} · 观察错误 {status.ObservableErrors} · 通知错误 {status.NotificationErrors}" +
          (string.IsNullOrWhiteSpace(status.LastError) ? "" : "\n" + status.LastError) +
          (status.AvaloniaSupport is { } framework ? $"\n框架指标 {framework.FrameworkVersion} · {framework.Source} · " +
              (framework.IsInitialized ? "已初始化；样本由真实布局和渲染产生" : "不可用：" + framework.Limitation) : "")
        : "尚未采集。名称支持 * 通配符，多项以逗号分隔。";
    public IReadOnlyList<double> MetricChartValues { get => _metricChartValues; private set => Set(ref _metricChartValues, value); }
    public string MetricChartSummary => _metricsStatus is { WindowStart: { } start, WindowEnd: { } end }
        ? $"所有图同步 UTC 时间轴 {start:HH:mm:ss.fff} — {end:HH:mm:ss.fff}；停止后冻结。统计只覆盖该时窗内受容量限制保留的原始观测。"
        : "开始采集后显示共享时间轴。";
    public MetricSeriesRowViewModel? SelectedMetricSeries
    {
        get => _selectedMetricSeries;
        set { if (Set(ref _selectedMetricSeries, value)) { UpdateMetricChart(); ClearMetricSeriesCommand?.Refresh(); } }
    }

    private void InitializeMetrics()
    {
        StartMetricsCommand = new AsyncCommand(StartMetricsCaptureAsync, ReportError, () => CanUseNativeTools && !_metricsMutationPending && _metricsStatus?.IsRunning != true);
        StopMetricsCommand = new AsyncCommand(StopMetricsCaptureAsync, ReportError, () => CanUseNativeTools && !_metricsMutationPending && _metricsStatus?.IsRunning == true);
        ClearMetricSeriesCommand = new AsyncCommand(() => ClearMetricSeriesAsync(SelectedMetricSeries!.Id), ReportError,
            () => CanUseNativeTools && SelectedMetricSeries is not null && !_metricsMutationPending);
        _client.MetricsSamplesAdded += HandleMetricsSamplesAdded;
        _metricsSnapshotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MetricsFrameIntervalMs) };
        _metricsSnapshotTimer.Tick += OnMetricsSnapshotTick;
        _metricsSnapshotTimer.Start();
    }

    private void RefreshMetricsCommands()
    {
        StartMetricsCommand?.Refresh();
        StopMetricsCommand?.Refresh();
        ClearMetricSeriesCommand?.Refresh();
        foreach (var chart in MetricCharts) chart.ClearCommand.Refresh();
    }

    private async Task StartMetricsCaptureAsync()
    {
        var request = new MetricsStartRequest(SplitFilters(MeterFilter), SplitFilters(InstrumentFilter),
            PollIntervalMs: MetricsPollIntervalMs, NotificationIntervalMs: MetricsNotificationIntervalMs,
            HistoryWindowMs: MetricsHistoryWindowMs, FrameIntervalMs: MetricsFrameIntervalMs,
            AggregateFrames: MetricsAggregateFrames);
        var version = BeginMetricsMutation();
        var session = _backend.Session?.SessionId;
        try
        {
            var status = await _client.StartMetricsAsync(request, _lifetime.Token);
            if (!MetricsOperationIsCurrent(version, session)) return;
            SetMetricsStatus(status);
            Status = "指标采集已开始；Histogram 帧内平均，Counter/Gauge 帧内末值；Counter 末值仍是原始增量。";
        }
        finally { EndMetricsMutation(version); }
        await RefreshMetricsAsync();
    }

    private async Task StopMetricsCaptureAsync()
    {
        var version = BeginMetricsMutation();
        var session = _backend.Session?.SessionId;
        try
        {
            var status = await _client.StopMetricsAsync(_lifetime.Token);
            if (!MetricsOperationIsCurrent(version, session)) return;
            SetMetricsStatus(status);
            Status = "指标采集已停止，时间窗和图表已冻结，仍可查看和清空。";
        }
        finally { EndMetricsMutation(version); }
        await RefreshMetricsAsync();
    }

    private async Task ClearMetricSeriesAsync(long id)
    {
        var version = BeginMetricsMutation();
        var session = _backend.Session?.SessionId;
        try
        {
            var status = await _client.ClearMetricsAsync(new MetricsClearRequest(id), _lifetime.Token);
            if (!MetricsOperationIsCurrent(version, session)) return;
            SetMetricsStatus(status);
            foreach (var row in MetricSamples.Where(row => row.SeriesId == id).ToArray())
            {
                MetricSamples.Remove(row);
                _metricSampleSequences.Remove(row.Sequence);
            }
            _metricFrames = _metricFrames.Where(frame => frame.SeriesId != id).ToArray();
            UpdateMetricChart();
            Status = $"已清空指标序列 #{id}，其他图及采集继续保留。";
        }
        finally { EndMetricsMutation(version); }
        await RefreshMetricsAsync();
    }

    private long BeginMetricsMutation()
    {
        _metricsMutationPending = true;
        _metricsRefreshing = false;
        _metricsRefreshId++;
        _metricsOperationVersion++;
        RefreshMetricsCommands();
        return _metricsOperationVersion;
    }

    private void EndMetricsMutation(long version)
    {
        if (version != _metricsOperationVersion) return;
        _metricsMutationPending = false;
        RefreshMetricsCommands();
    }

    private bool MetricsOperationIsCurrent(long version, string? session) =>
        !_disposed && CanUseNativeTools && version == _metricsOperationVersion && session == _backend.Session?.SessionId;

    public async Task RefreshMetricsAsync()
    {
        if (!CanUseNativeTools || _metricsRefreshing || _metricsMutationPending) return;
        _metricsRefreshing = true;
        var version = _metricsOperationVersion;
        var refreshId = ++_metricsRefreshId;
        var sessionId = _backend.Session?.SessionId;
        try
        {
            var snapshot = await _client.GetMetricsSnapshotAsync(_lifetime.Token);
            if (!MetricsOperationIsCurrent(version, sessionId) || refreshId != _metricsRefreshId ||
                !CanAcceptMetricsStatus(snapshot.Status)) return;
            SetMetricsStatus(snapshot.Status);
            Replace(MetricInstruments, snapshot.Instruments);
            MetricSamples.Clear();
            _metricSampleSequences.Clear();
            foreach (var sample in snapshot.Samples) AddMetricSample(sample, true);
            _metricsSnapshotSequence = snapshot.Samples.Count == 0 ? 0 : snapshot.Samples.Max(sample => sample.Sequence);
            _metricFrames = snapshot.Frames ?? [];
            var selectedId = SelectedMetricSeries?.Id;
            var existing = MetricSeries.ToDictionary(row => row.Id);
            var ids = snapshot.Series.Select(series => series.Id).ToHashSet();
            for (var index = MetricSeries.Count - 1; index >= 0; index--)
                if (!ids.Contains(MetricSeries[index].Id)) MetricSeries.RemoveAt(index);
            for (var index = 0; index < snapshot.Series.Count; index++)
            {
                var series = snapshot.Series[index];
                var instrument = snapshot.Instruments.FirstOrDefault(item => item.Id == series.InstrumentId);
                if (existing.TryGetValue(series.Id, out var row)) row.Refresh(series, instrument);
                else row = new MetricSeriesRowViewModel(series, instrument);
                var currentIndex = MetricSeries.IndexOf(row);
                if (currentIndex < 0) MetricSeries.Insert(index, row);
                else if (currentIndex != index) MetricSeries.Move(currentIndex, index);
            }
            SelectedMetricSeries = MetricSeries.FirstOrDefault(series => series.Id == selectedId) ?? MetricSeries.FirstOrDefault();
            UpdateMetricChart();
        }
        finally { if (refreshId == _metricsRefreshId) _metricsRefreshing = false; }
    }

    private void HandleMetricsSamplesAdded(MetricsSamplesAddedNotification notification)
    {
        var sessionId = _backend.Session?.SessionId;
        var version = _metricsOperationVersion;
        Dispatch(() =>
        {
            if (!MetricsOperationIsCurrent(version, sessionId) || _metricsMutationPending ||
                !CanAcceptMetricsStatus(notification.Status)) return;
            if (_metricsStatus is { IsRunning: false, CaptureId: { } stoppedId } && stoppedId == notification.Status.CaptureId) return;
            SetMetricsStatus(notification.Status);
            foreach (var instrument in notification.Instruments)
            {
                var index = MetricInstruments.ToList().FindIndex(item => item.Id == instrument.Id);
                if (index >= 0) MetricInstruments[index] = instrument;
                else MetricInstruments.Add(instrument);
            }
            foreach (var sample in notification.Samples) AddMetricSample(sample);
            // Frame display follows the configured refresh timer. Raw notification batches
            // never supply a second, truncated implementation of histogram statistics.
        });
    }

    private bool CanAcceptMetricsStatus(MetricsStatusDto status) => _metricsStatus is not { } current ||
        (current.CaptureId is null || status.CaptureId == current.CaptureId) && status.Revision >= current.Revision &&
        (current.WindowEnd is null || status.WindowEnd >= current.WindowEnd);

    private void AddMetricSample(MetricSampleDto sample, bool fromSnapshot = false)
    {
        if (!fromSnapshot && sample.Sequence <= _metricsSnapshotSequence) return;
        if (!_metricSampleSequences.Add(sample.Sequence)) return;
        var capacity = _metricsStatus?.Capacity ?? 512;
        while (MetricSamples.Count >= capacity)
        {
            _metricSampleSequences.Remove(MetricSamples[0].Sequence);
            MetricSamples.RemoveAt(0);
        }
        var instrument = MetricInstruments.FirstOrDefault(item => item.Id == sample.InstrumentId);
        MetricSamples.Add(new MetricSampleRowViewModel(sample, instrument?.Name ?? $"#{sample.InstrumentId}"));
    }

    private void SetMetricsStatus(MetricsStatusDto status)
    {
        if (status.CaptureId != _metricsStatus?.CaptureId)
            ClearMetricsData();
        _metricsStatus = status;
        if (_metricsSnapshotTimer is { } timer && timer.Interval != TimeSpan.FromMilliseconds(status.FrameIntervalMs))
            timer.Interval = TimeSpan.FromMilliseconds(status.FrameIntervalMs);
        Raise(nameof(CurrentMetricsStatus));
        Raise(nameof(MetricsSummary));
        RefreshMetricsCommands();
    }

    private void UpdateMetricChart()
    {
        MetricChartValues = SelectedMetricSeries is { } series
            ? _metricFrames.Where(frame => frame.SeriesId == series.Id && frame.Value.HasValue).Select(frame => frame.Value!.Value).ToArray() : [];
        if (_metricsStatus is { WindowStart: { } start, WindowEnd: { } end, CaptureId: var capture })
        {
            var existing = MetricCharts.ToDictionary(row => row.Id);
            var ids = MetricSeries.Select(row => row.Id).ToHashSet();
            // Keep surviving chart controls and their pointer captures attached during live refresh.
            for (var index = MetricCharts.Count - 1; index >= 0; index--)
                if (!ids.Contains(MetricCharts[index].Id)) MetricCharts.RemoveAt(index);
            for (var index = 0; index < MetricSeries.Count; index++)
            {
                var row = MetricSeries[index];
                var name = row.Tags.Length == 0 ? row.Name : $"{row.Name} · {row.Tags}";
                var points = _metricFrames.Where(frame => frame.SeriesId == row.Id && frame.Value.HasValue)
                    .Select(frame => new MetricChartPoint(frame.Start < start ? start : frame.Start, frame.Value!.Value)).ToArray();
                if (existing.TryGetValue(row.Id, out var chart)) chart.Refresh(name, points, start, end);
                else chart = new MetricChartRowViewModel(row.Id, name, points, start, end,
                    new AsyncCommand(() => ClearMetricSeriesAsync(row.Id), ReportError,
                        () => CanUseNativeTools && !_metricsMutationPending && _metricsStatus?.CaptureId == capture &&
                              MetricSeries.Any(current => current.Id == row.Id)));
                var currentIndex = MetricCharts.IndexOf(chart);
                if (currentIndex < 0) MetricCharts.Insert(index, chart);
                else if (currentIndex != index) MetricCharts.Move(currentIndex, index);
            }
        }
        Raise(nameof(MetricChartSummary));
    }

    private void ClearMetricsData()
    {
        MetricInstruments.Clear();
        MetricSeries.Clear();
        MetricSamples.Clear();
        MetricCharts.Clear();
        _metricFrames = [];
        _metricSampleSequences.Clear();
        _metricsSnapshotSequence = 0;
        SelectedMetricSeries = null;
        MetricChartValues = [];
    }

    private void ResetMetrics()
    {
        _metricsOperationVersion++;
        _metricsRefreshId++;
        _metricsRefreshing = _metricsMutationPending = false;
        ClearMetricsData();
        _metricsStatus = null;
        Raise(nameof(CurrentMetricsStatus));
        Raise(nameof(MetricsSummary));
        RefreshMetricsCommands();
    }

    private void OnMetricsSnapshotTick(object? sender, EventArgs args)
    {
        if (InspectionTabIndex == 6 && CanUseNativeTools && _metricsStatus?.IsRunning == true)
            _ = RunSafelyAsync(RefreshMetricsAsync);
    }

    private void DisposeMetrics()
    {
        _client.MetricsSamplesAdded -= HandleMetricsSamplesAdded;
        if (_metricsSnapshotTimer is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnMetricsSnapshotTick;
        }
    }

    private static string[] SplitFilters(string source) =>
        source.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

public sealed class MetricSeriesRowViewModel(MetricSeriesDto series, MetricInstrumentDto? instrument) : ObservableObject
{
    private MetricSeriesDto _series = series;
    private MetricInstrumentDto? _instrument = instrument;
    public long Id => _series.Id;
    public string Name => _instrument is null ? $"仪表 #{_series.InstrumentId}" : $"{_instrument.MeterName} / {_instrument.Name}";
    public string Tags => string.Join(", ", _series.Tags.Select(tag => $"{tag.Key}={tag.Value}"));
    public int WindowCount => _series.WindowCount;
    public string Last => Format(_series.LastValue);
    public string Minimum => Format(_series.WindowMin);
    public string Maximum => Format(_series.WindowMax);
    public string Mean => Format(_series.WindowMean);
    public string P50 => Format(_series.WindowP50);
    public string P90 => Format(_series.WindowP90);
    public string P95 => Format(_series.WindowP95);
    public string Scope => $"{_series.WindowScope} · {_series.PercentileAlgorithm}";
    public void Refresh(MetricSeriesDto updated, MetricInstrumentDto? updatedInstrument)
    {
        _series = updated;
        _instrument = updatedInstrument;
        foreach (var name in new[] { nameof(Name), nameof(Tags), nameof(WindowCount), nameof(Last), nameof(Minimum),
                     nameof(Maximum), nameof(Mean), nameof(P50), nameof(P90), nameof(P95), nameof(Scope) }) Raise(name);
    }
    private static string Format(double? value) => value?.ToString("G6", CultureInfo.InvariantCulture) ?? "—";
}

public sealed class MetricChartRowViewModel(long id, string name, IReadOnlyList<MetricChartPoint> points,
    DateTimeOffset windowStart, DateTimeOffset windowEnd, AsyncCommand clearCommand) : ObservableObject
{
    private string _name = name;
    private IReadOnlyList<MetricChartPoint> _points = points;
    private DateTimeOffset _windowStart = windowStart, _windowEnd = windowEnd;
    public long Id { get; } = id;
    public string Name => _name;
    public IReadOnlyList<MetricChartPoint> Points => _points;
    public DateTimeOffset WindowStart => _windowStart;
    public DateTimeOffset WindowEnd => _windowEnd;
    public AsyncCommand ClearCommand { get; } = clearCommand;
    public string Axis => $"UTC {WindowStart:HH:mm:ss.fff} — {WindowEnd:HH:mm:ss.fff} · {Points.Count} 点";
    public void Refresh(string updatedName, IReadOnlyList<MetricChartPoint> updatedPoints,
        DateTimeOffset updatedStart, DateTimeOffset updatedEnd)
    {
        Set(ref _name, updatedName, nameof(Name));
        Set(ref _points, updatedPoints, nameof(Points));
        Set(ref _windowStart, updatedStart, nameof(WindowStart));
        Set(ref _windowEnd, updatedEnd, nameof(WindowEnd));
        Raise(nameof(Axis));
    }
}

public sealed class MetricSampleRowViewModel(MetricSampleDto sample, string name)
{
    public long Sequence => sample.Sequence;
    public long SeriesId => sample.SeriesId;
    public DateTimeOffset Timestamp => sample.Timestamp;
    public string Name => name;
    public double Value => sample.Value;
    public string RawValue => sample.RawValue;
    public string NumericType => sample.NumericType;
    public string Tags => string.Join(", ", sample.Tags.Select(tag => $"{tag.Key}={tag.Value}"));
}

