using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Protocol;

/// <summary>Case-insensitive substring predicates, all applied before an entry enters capture history.</summary>
public sealed record LogFilter(
    string? Category = null,
    string? Message = null,
    string? ParameterName = null,
    string? ParameterValue = null);

[JsonConverter(typeof(JsonStringEnumConverter<LogValueState>))]
public enum LogValueState
{
    Known,
    Null,
    Unavailable
}

/// <summary>A bounded scalar snapshot. Unknown objects are never formatted by calling business code.</summary>
public sealed record LogParameterInfo(
    string Name,
    string TypeName,
    string? Value,
    LogValueState State,
    bool IsTruncated = false);

public sealed record LogExceptionInfo(
    string TypeName,
    string Message,
    string? StackTrace,
    IReadOnlyList<LogExceptionInfo> InnerExceptions,
    bool IsTruncated = false,
    bool IsCycle = false);

public sealed record LogScopeInfo(IReadOnlyList<LogParameterInfo> Parameters, bool IsTruncated = false);

public sealed record LogDetails(
    string? MessageTemplate,
    IReadOnlyList<LogParameterInfo> Parameters,
    LogValueState ParametersState,
    LogExceptionInfo? Exception,
    string? SourceType,
    string Origin,
    int? EventId = null,
    string? EventName = null,
    IReadOnlyList<LogScopeInfo>? Scopes = null,
    bool ParametersTruncated = false,
    bool TemplateTruncated = false,
    bool ByteBudgetTruncated = false);
