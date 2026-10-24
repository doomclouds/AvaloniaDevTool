namespace AvaloniaDevTool.Protocol;

/// <summary>Distinguishes observed null, unknown, unsupported, and inapplicable diagnostic facts.</summary>
public enum BindingDetailState { Known, Null, Unknown, Unavailable, NotApplicable }

/// <summary>A bounded, read-only diagnostic value. ObjectId is an optional weak navigation handle.</summary>
public sealed record BindingDetailValue(BindingDetailState State, string? Value = null, string? TypeName = null,
    long? ObjectId = null, string? Reason = null);

public sealed record BindingDiagnosticGap(string Field, string Reason);

/// <summary>A bounded exception tree; it never transports a live exception or its Data dictionary.</summary>
public sealed record BindingErrorInfo(string Category, string Message, string? ExceptionType = null,
    IReadOnlyList<BindingErrorInfo>? InnerErrors = null, bool Truncated = false);

/// <summary>Structured binding evidence. Missing facts must carry an explicit state and gap.</summary>
public sealed record BindingDetails(string Kind, BindingDetailValue SourceType, BindingDetailValue Source,
    BindingDetailValue Path, BindingDetailValue Mode, BindingDetailValue Converter,
    BindingDetailValue ConverterParameter, BindingDetailValue FallbackValue, BindingDetailValue TargetNullValue,
    BindingDetailValue UpdateSourceTrigger, BindingDetailValue StringFormat, BindingDetailValue ElementName,
    BindingDetailValue RelativeSource, IReadOnlyList<BindingDetails> Children, IReadOnlyList<BindingErrorInfo> Errors,
    IReadOnlyList<BindingDiagnosticGap> Gaps, bool Truncated = false,
    BindingDetailValue? DataContext = null, BindingDetailValue? DataContextOrigin = null,
    BindingDetailValue? CurrentValue = null, BindingDetailValue? ResourceKey = null,
    BindingDetailValue? ConverterCulture = null, BindingDetailValue? ConverterCultureOrigin = null);
