using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace AvaloniaDevTool.Protocol;

public static class DiagnosticsProtocol
{
    public const int Version = 1;
    public const string PipePrefix = "avalonia-devtool-";
}

public static class Methods
{
    public const string SessionInfo = "session.info";
    public const string TreeRoots = "tree.roots";
    public const string TreeChildren = "tree.children";
    public const string PropertiesGet = "properties.get";
    public const string PropertiesSet = "properties.set";
    public const string PropertiesClear = "properties.clear";
    public const string Highlight = "selection.highlight";
    public const string SetPicking = "selection.pick";
    public const string SelectionChanged = "selection.changed";
    public const string TreeInvalidated = "tree.invalidated";
    public const string PropertiesChanged = "properties.changed";
    public const string PickingChanged = "picking.changed";
}

[JsonConverter(typeof(JsonStringEnumConverter<TreeKind>))]
public enum TreeKind { Visual, Logical, Merged }

[JsonConverter(typeof(JsonStringEnumConverter<TreeNodeRole>))]
public enum TreeNodeRole { Element, TemplateGroup, TemplateRoot, TemplatePart, ContentSlot }

public sealed record SessionInfo(string SessionId, string ApplicationName, int ProcessId, string AvaloniaVersion, int ProtocolVersion);
public sealed record TreeNodeInfo(long Id, string TypeName, string? Name, string? Preview, bool HasChildren,
    TreeNodeRole Role = TreeNodeRole.Element, long? TemplateOwnerId = null, long? SlotContentId = null);
public sealed record PropertyInfoDto(string Id, string Name, string DeclaringType, string TypeName,
    string? Value, string ValueSource, bool IsReadOnly, bool CanEdit, bool CanClear, string? Error = null,
    PropertyPresentation? Presentation = null);
public sealed record TreeRootsRequest(TreeKind Kind);
public sealed record TreeChildrenRequest(long ObjectId, TreeKind Kind);
public sealed record ObjectRequest(long ObjectId);
public sealed record SetPropertyRequest(long ObjectId, string PropertyId, string Value);
public sealed record ClearPropertyRequest(long ObjectId, string PropertyId);
public sealed record HighlightRequest(long? ObjectId);
public sealed record PickingRequest(bool Enabled);
public sealed record SelectionChangedNotification(long ObjectId);
public sealed record TreeInvalidatedNotification(long? ParentId = null);
public sealed record PropertiesChangedNotification(long ObjectId);
public sealed record PickingChangedNotification(bool IsPicking);
public sealed record OperationResult(bool Success = true);

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The reflection resolver is used only when System.Text.Json's linker feature switch enables reflection; Native AOT removes this branch.")]
    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = RuntimeFeature.IsDynamicCodeSupported && JsonSerializer.IsReflectionEnabledByDefault
            ? JsonTypeInfoResolver.Combine(ProtocolJsonContext.Default, new DefaultJsonTypeInfoResolver())
            : ProtocolJsonContext.Default
    };

    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));

    public static byte[] SerializeToUtf8Bytes(object? value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options.GetTypeInfo(value?.GetType() ?? typeof(object)));

    public static JsonElement ToElement(object? value) =>
        JsonSerializer.SerializeToElement(value, Options.GetTypeInfo(value?.GetType() ?? typeof(object)));

    public static T Read<T>(JsonElement value) =>
        value.Deserialize(TypeInfo<T>()) ?? throw new RpcException("invalid_request", "The request payload is missing.");
}

public sealed class RpcException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

