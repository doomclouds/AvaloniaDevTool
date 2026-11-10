using AvaloniaDevTool.Client;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace AvaloniaDevTool.Cli;

[McpServerToolType]
public sealed class ResourceMutationTools(ToolSession session) : TargetToolBase(session)
{
    [McpServerTool(Name = "set-resource"),
     Description(
         "Temporarily edit an existing string-key resource. On detach it is restored only if the application has not replaced the edited value.")]
    public async Task<string> SetResource(long dictionaryId,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).Resources.SetResourceAsync(dictionaryId,
            key,
            value,
            cancellationToken);
        return "Resource edit applied";
    }

    [McpServerTool(Name = "reset-resource"),
     Description("Restore this session's original resource value, preserving a newer application write on conflict.")]
    public async Task<string> ResetResource(long dictionaryId, string key, CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).Resources.ResetResourceAsync(dictionaryId,
            key,
            cancellationToken);
        return "Resource edit reset";
    }

    [McpServerTool(Name = "resource-tree", ReadOnly = true),
     Description(
         "Read the bounded resource provider hierarchy with explicit shared references, cycles, enumeration support and limitations.")]
    public async Task<ResourceTreeSnapshot> ResourceTree(long? nodeId = null,
        int maxNodes = 512,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).Resources.QueryResourceTreeAsync(new(nodeId, maxNodes),
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
        await (await session.GetAsync(cancellationToken)).Resources.QueryResourceEntriesAsync(
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
        await (await session.GetAsync(cancellationToken)).Resources.ReadResourceEntryAsync(
            new(dictionaryId, keyObjectId),
            cancellationToken);

    [McpServerTool(Name = "export-asset"),
     Description(
         "Export a complete embedded target asset to an absolute path on this MCP server's computer. Uses bounded chunks and verifies byte length and SHA-256; the preview truncation limit does not truncate exported files. Existing files require explicit overwrite=true.")]
    public async Task<AssetExportResult> ExportAsset(string uri,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(destinationPath))
            throw new McpException("invalid_path: destinationPath must be an absolute local path.");
        return await (await session.GetAsync(cancellationToken)).Assets.ExportAssetToFileAsync(uri,
            destinationPath,
            overwrite,
            cancellationToken: cancellationToken);
    }
}
