using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "resource-tree", ReadOnly = true),
     Description(
         "Read the bounded resource provider hierarchy with explicit shared references, cycles, enumeration support and limitations.")]
    public async Task<ResourceTreeSnapshot> ResourceTree(long? nodeId = null,
        int maxNodes = 512,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).QueryResourceTreeAsync(new(nodeId, maxNodes),
            cancellationToken);

    [McpServerTool(Name = "resource-query", ReadOnly = true),
     Description(
         "Page resource entries with optional nested providers, key/type filters and source provenance. Duplicate keys retain their distinct owner; this is not a resource winner calculation.")]
    public async Task<ResourceEntriesSnapshot> ResourceQuery(long dictionaryId,
        bool includeNested = false,
        string? keyFilter = null,
        string? typeFilter = null,
        ResourceQuerySort sort = ResourceQuerySort.Key,
        bool descending = false,
        int offset = 0,
        int limit = 50,
        int maxNodes = 512,
        int maxEntries = 10000,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).QueryResourceEntriesAsync(
            new(dictionaryId,
                includeNested,
                keyFilter,
                typeFilter,
                sort,
                descending,
                offset,
                limit,
                maxNodes,
                maxEntries),
            cancellationToken);

    [McpServerTool(Name = "resource-entry", ReadOnly = true),
     Description(
         "Read an existing resource entry using its actual dictionary and weak key identity, including non-string keys.")]
    public async Task<ResourceQueryEntryInfo> ResourceEntry(long dictionaryId,
        long keyObjectId,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).ReadResourceEntryAsync(new(dictionaryId, keyObjectId),
            cancellationToken);
}
