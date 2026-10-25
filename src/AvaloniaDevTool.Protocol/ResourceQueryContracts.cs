using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Protocol;

public static class ResourceQueryMethods
{
    public const string Trees = "inspection.resourceTreeQuery";
    public const string Entries = "inspection.resourceEntriesQuery";
    public const string ReadEntry = "inspection.resourceReadEntry";
}

[JsonConverter(typeof(JsonStringEnumConverter<ResourceQuerySort>))]
public enum ResourceQuerySort { Key, Type, Source }

public sealed record ResourceTreeQueryRequest(long? ObjectId = null, int MaxNodes = 512, bool OnlySelf = false);
public sealed record ResourceProviderInfo(long Id, string Name, string Scope, string? Theme, string Path,
    string TypeName, int EntryCount, bool CanEnumerate, bool IsReference, bool IsCycle,
    IReadOnlyList<ResourceProviderInfo> Children);
public sealed record ResourceTreeSnapshot(IReadOnlyList<ResourceProviderInfo> Roots, int NodesVisited,
    bool Truncated, IReadOnlyList<string> Limitations);

public sealed record ResourceEntriesQueryRequest(long DictionaryId, bool IncludeNested = false,
    string? KeyFilter = null, string? TypeFilter = null, ResourceQuerySort Sort = ResourceQuerySort.Key,
    bool Descending = false, int Offset = 0, int Limit = 50, int MaxNodes = 512, int MaxEntries = 10000);
public sealed record ResourceQueryEntryInfo(long OwnerDictionaryId, string ProviderPath, string Scope, string? Theme,
    string Key, string KeyType, string? StringKey, long KeyObjectId, string TypeName, string? Value,
    long? ValueObjectId, bool CanEdit, bool IsOverridden, bool KeyTruncated, bool ValueTruncated);
public sealed record ResourceEntriesSnapshot(long DictionaryId, int Offset, int TotalCount,
    IReadOnlyList<ResourceQueryEntryInfo> Entries, bool HasMore, bool Truncated, int NodesVisited,
    int EntriesVisited, IReadOnlyList<string> Limitations);
public sealed record ResourceReadEntryRequest(long DictionaryId, long KeyObjectId);
