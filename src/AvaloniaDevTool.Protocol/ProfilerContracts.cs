using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Protocol;

public static class ProfilerMethods
{
    public const string Start = "profiler.start";
    public const string Stop = "profiler.stop";
    public const string Clear = "profiler.clear";
    public const string Status = "profiler.status";
    public const string Snapshot = "profiler.snapshot";
    public const string SamplesAdded = "profiler.samples-added";
}

[JsonConverter(typeof(JsonStringEnumConverter<ProfilerCategory>))]
public enum ProfilerCategory { StyleAttach, StyleEvaluate, ResourceLookup }

public sealed record ProfilerStartRequest(
    IReadOnlyList<ProfilerCategory>? Categories = null,
    string? Filter = null,
    int Capacity = 512,
    int PendingCapacity = 256,
    int MaxGroups = 128,
    int MaxInFlight = 256,
    int MaxTags = 8,
    int NotificationIntervalMs = 200);

public sealed record ProfilerTagDto(string Key, string? Value);

public sealed record ProfilerSampleDto(long Sequence, long GroupId, ProfilerCategory Category,
    string Operation, DateTimeOffset Timestamp, double DurationMicroseconds,
    string? Style, string? Selector, string? Key, string? ThemeVariant, string? Result,
    bool? IsActive, bool? IsMatch, bool FastReject, IReadOnlyList<ProfilerTagDto> Tags);

public sealed record ProfilerGroupDto(long Id, ProfilerCategory Category, string Name,
    int WindowCount, double TotalMicroseconds, double MinMicroseconds,
    double MaxMicroseconds, double MeanMicroseconds,
    int MatchCount, int NoMatchCount, int UnknownCount, int FastRejectCount,
    string WindowScope = "retained_samples");

public sealed record ProfilerStatusDto(bool IsRunning, string? CaptureId,
    int Capacity, int PendingCapacity, int MaxGroups, int MaxInFlight, int MaxTags,
    int NotificationIntervalMs, int GroupCount, int BufferedSampleCount, int PendingSampleCount,
    int InFlightCount, long StartedOperations, long CompletedOperations, long FilteredOperations,
    long EvictedHistorySamples, long DroppedPendingSamples, long DroppedGroupSamples,
    long DroppedInFlightOperations, long IncompleteOperations, long TruncatedTagSets,
    long CallbackErrors, long NotificationErrors, string? LastError,
    IReadOnlyList<ProfilerCategory>? Categories = null, string? Filter = null,
    string SourceName = "Avalonia.Diagnostic.Source", string DurationUnit = "microseconds");

public sealed record ProfilerSnapshotDto(ProfilerStatusDto Status,
    IReadOnlyList<ProfilerGroupDto> Groups, IReadOnlyList<ProfilerSampleDto> Samples);

public sealed record ProfilerSamplesAddedNotification(IReadOnlyList<ProfilerSampleDto> Samples,
    ProfilerStatusDto Status);
