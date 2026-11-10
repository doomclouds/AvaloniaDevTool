using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Protocol;

public static class PropertyPresentationMethods
{
    public const string Preview = "properties.preview";
}

[JsonConverter(typeof(JsonStringEnumConverter<PropertyEditorKind>))]
public enum PropertyEditorKind
{
    Text,
    Boolean,
    Number,
    Enum,
    Flags,
    Color,
    SolidColorBrush,
    ReadOnly,
    Brush
}

[JsonConverter(typeof(JsonStringEnumConverter<PropertyPreviewKind>))]
public enum PropertyPreviewKind
{
    None,
    Image,
    Geometry
}

[JsonConverter(typeof(JsonStringEnumConverter<PropertyPreviewStatus>))]
public enum PropertyPreviewStatus
{
    Available,
    NotAvailable,
    Stale,
    InvalidValue
}

public sealed record PropertyEnumChoice(string Name, string Value);

public sealed record PropertiesRequest(long ObjectId, bool IncludeClr = false, bool Contextual = false);

public sealed record PropertyContextInfo(bool IsApplicable, string Rule, string? VisualParentType);

public sealed record PropertyPresentation(
    string Category,
    string MemberKind,
    PropertyEditorKind Editor,
    IReadOnlyList<PropertyEnumChoice>? Choices = null,
    PropertyPreviewKind Preview = PropertyPreviewKind.None,
    long? ValueId = null,
    long Version = 0,
    string? Notice = null,
    BrushDto? Brush = null,
    PropertyContextInfo? Context = null);

public sealed record PropertyPreviewRequest(
    long ObjectId,
    string PropertyId,
    long ValueId,
    long Version,
    int MaxWidth = 320,
    int MaxHeight = 240);

public sealed record PropertyPreviewResult(
    PropertyPreviewStatus Status,
    string? Base64 = null,
    int Width = 0,
    int Height = 0,
    string? Message = null);
