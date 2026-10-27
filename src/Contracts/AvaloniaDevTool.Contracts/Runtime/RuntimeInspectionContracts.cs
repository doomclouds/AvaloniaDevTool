using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Protocol;

public static class RuntimeInspectionMethods
{
    public const string Members = "runtime.members";
    public const string Items = "runtime.items";
}

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeMemberKind>))]
public enum RuntimeMemberKind
{
    Property,
    Field
}

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeCollectionKind>))]
public enum RuntimeCollectionKind
{
    None,
    List,
    Dictionary,
    UnsupportedEnumerable
}

public sealed record RuntimeMembersRequest(long ObjectId, int Offset = 0, int Limit = 50);

public sealed record RuntimeItemsRequest(long ObjectId, int Offset = 0, int Limit = 50);

public sealed record RuntimeValueInfo(
    string TypeName,
    string Preview,
    long? ValueObjectId,
    bool IsNull,
    bool Truncated,
    bool IsSelfReference = false);

public sealed record RuntimeMemberInfo(
    string MemberId,
    string Name,
    RuntimeMemberKind Kind,
    string DeclaringType,
    string DeclaredType,
    RuntimeValueInfo Value,
    bool IsReadOnly = true,
    string? Error = null,
    bool ErrorTruncated = false);

public sealed record RuntimeObjectSnapshot(
    long ObjectId,
    string TypeName,
    int Offset,
    int TotalCount,
    IReadOnlyList<RuntimeMemberInfo> Members,
    bool HasMore,
    RuntimeCollectionKind CollectionKind);

public sealed record RuntimeItemInfo(
    int Index,
    RuntimeValueInfo? Key,
    RuntimeValueInfo Value,
    string? Error = null,
    bool ErrorTruncated = false);

public sealed record RuntimeItemsSnapshot(
    long ObjectId,
    string TypeName,
    RuntimeCollectionKind Kind,
    int Offset,
    int TotalCount,
    IReadOnlyList<RuntimeItemInfo> Items,
    bool HasMore);
