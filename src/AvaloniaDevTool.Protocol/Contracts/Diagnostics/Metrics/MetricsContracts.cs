namespace AvaloniaDevTool.Protocol;

public static class MetricsMethods
{
    public const string Start = "metrics.start";
    public const string Stop = "metrics.stop";
    public const string Snapshot = "metrics.snapshot";
    public const string Status = "metrics.status";
    public const string Clear = "metrics.clear";
    public const string SampleAdded = "metrics.samples-added";
}

public sealed record MetricsStartRequest(
    IReadOnlyList<string>? MeterNames = null,
    IReadOnlyList<string>? InstrumentNames = null,
    int Capacity = 512,
    int PendingCapacity = 256,
    int MaxSeries = 64,
    int MaxInstruments = 128,
    int MaxTags = 8,
    int PollIntervalMs = 1000,
    int NotificationIntervalMs = 200,
    int HistoryWindowMs = 60000,
    int FrameIntervalMs = 250,
    bool AggregateFrames = true);

public sealed record MetricsClearRequest(long? SeriesId = null);

public sealed record MetricTagDto(string Key, string? Value);

public sealed record MetricInstrumentDto(
    long Id,
    string MeterName,
    string? MeterVersion,
    string Name,
    string? Description,
    string? Unit,
    string Kind,
    bool IsObservable,
    string ValueSemantics,
    bool IsActive);

public sealed record MetricSampleDto(
    long Sequence,
    long SeriesId,
    long InstrumentId,
    DateTimeOffset Timestamp,
    double Value,
    string NumericType,
    string RawValue,
    IReadOnlyList<MetricTagDto> Tags);

public sealed record MetricSeriesDto(
    long Id,
    long InstrumentId,
    IReadOnlyList<MetricTagDto> Tags,
    int WindowCount,
    double? WindowMin,
    double? WindowMax,
    double? WindowMean,
    double? WindowP50,
    double? WindowP90,
    double? WindowP95,
    double? LastValue,
    string WindowScope = "retained_time_window",
    string PercentileAlgorithm = "nearest_rank");

public sealed record MetricFrameDto(
    long SeriesId,
    DateTimeOffset Start,
    DateTimeOffset End,
    int Count,
    double Minimum,
    double Maximum,
    double? Mean,
    double P50,
    double P90,
    double P95,
    double? Value,
    string Aggregation);

public sealed record MetricsStatusDto(
    bool IsRunning,
    string? CaptureId,
    int Capacity,
    int PendingCapacity,
    int MaxSeries,
    int MaxInstruments,
    int MaxTags,
    int PollIntervalMs,
    int NotificationIntervalMs,
    int InstrumentCount,
    int SeriesCount,
    int BufferedSampleCount,
    int PendingSampleCount,
    long ReceivedSamples,
    long DroppedPendingSamples,
    long EvictedHistorySamples,
    long DroppedSeriesSamples,
    long DroppedInstruments,
    long TruncatedTagSets,
    long InvalidMeasurements,
    long ObservableErrors,
    long NotificationErrors,
    string? LastError,
    IReadOnlyList<string>? MeterNames = null,
    IReadOnlyList<string>? InstrumentNames = null,
    AvaloniaMetricsSupportDto? AvaloniaSupport = null,
    int HistoryWindowMs = 60000,
    int FrameIntervalMs = 250,
    DateTimeOffset? WindowStart = null,
    DateTimeOffset? WindowEnd = null,
    long ExpiredHistorySamples = 0,
    long Revision = 0,
    long LastSequence = 0,
    bool AggregateFrames = true);

public sealed record MetricsSnapshotDto(
    MetricsStatusDto Status,
    IReadOnlyList<MetricInstrumentDto> Instruments,
    IReadOnlyList<MetricSeriesDto> Series,
    IReadOnlyList<MetricSampleDto> Samples,
    IReadOnlyList<MetricFrameDto>? Frames = null);

public sealed record MetricsSamplesAddedNotification(
    IReadOnlyList<MetricSampleDto> Samples,
    IReadOnlyList<MetricInstrumentDto> Instruments,
    MetricsStatusDto Status);
