namespace AvaloniaDevTool.Protocol;

public sealed record PropertyInfoDto(
    string Id,
    string Name,
    string DeclaringType,
    string TypeName,
    string? Value,
    string ValueSource,
    bool IsReadOnly,
    bool CanEdit,
    bool CanClear,
    string? Error = null,
    PropertyPresentation? Presentation = null,
    bool HasClrWrapper = false);

public sealed record SetPropertyRequest(long ObjectId, string PropertyId, string Value);

public sealed record ClearPropertyRequest(long ObjectId, string PropertyId);

public sealed record PropertiesChangedNotification(long ObjectId);
