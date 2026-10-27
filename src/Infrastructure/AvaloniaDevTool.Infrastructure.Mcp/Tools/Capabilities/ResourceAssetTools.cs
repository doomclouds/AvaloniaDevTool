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
public sealed class ResourceAssetTools(ToolSession session, ManagedToolContext context)
    : ManagedToolBase(session, context)
{
    private const int ResourceEntryLimit = 1000;
    private const int AssetDownloadLimit = 16 * 1024 * 1024;

    [McpServerTool(Name = "resources", ReadOnly = true, Destructive = false),
     Description("Gets resources. Optionally scoped to a node.")]
    public Task<ResourceInspectionResult> Resources(long nodeId = 0,
        bool onlySelf = false,
        CancellationToken cancellationToken = default) => WithClientAsync(cancellationToken,
        async (client, token) =>
        {
            var nativeId = NativeNodeId(nodeId);
            var tree = await client.Resources.QueryResourceTreeAsync(new ResourceTreeQueryRequest(
                    nativeId == 0 ? null : nativeId,
                    MaxNodes: 128,
                    OnlySelf: onlySelf),
                token).ConfigureAwait(false);
            var entries = new List<ResourceQueryEntryInfo>();
            var notes = tree.Limitations.ToHashSet(StringComparer.Ordinal);
            var truncated = tree.Truncated;
            var visited = new HashSet<long>();
            foreach (var provider in FlattenResourceProviders(tree.Roots))
            {
                if (!provider.CanEnumerate || !visited.Add(provider.Id))
                    continue;
                if (entries.Count >= ResourceEntryLimit)
                {
                    truncated = true;
                    break;
                }

                var page = await client.Resources.QueryResourceEntriesAsync(new ResourceEntriesQueryRequest(provider.Id,
                        Limit: Math.Min(200, ResourceEntryLimit - entries.Count)),
                    token).ConfigureAwait(false);
                entries.AddRange(page.Entries);
                truncated |= page.Truncated;
                foreach (var note in page.Limitations)
                    notes.Add(note);
                while (page.HasMore && entries.Count < ResourceEntryLimit)
                {
                    page = await client.Resources.QueryResourceEntriesAsync(new ResourceEntriesQueryRequest(provider.Id,
                            Offset: page.Offset + page.Entries.Count,
                            Limit: Math.Min(200, ResourceEntryLimit - entries.Count)),
                        token).ConfigureAwait(false);
                    entries.AddRange(page.Entries);
                    truncated |= page.Truncated;
                    foreach (var note in page.Limitations)
                        notes.Add(note);
                }

                truncated |= page.HasMore;
            }

            if (truncated)
                notes.Add(
                    "Resource results are bounded to 128 providers and 1000 entries; clipped values and truncation are explicit.");
            return new ResourceInspectionResult(PublicNodeId(nativeId),
                onlySelf,
                tree with { Roots = tree.Roots.Select(PublicResourceProvider).ToArray() },
                entries.Select(entry => entry with
                {
                    OwnerDictionaryId = PublicNodeId(entry.OwnerDictionaryId),
                    KeyObjectId = PublicNodeId(entry.KeyObjectId), ValueObjectId = PublicNodeId(entry.ValueObjectId)
                }).ToArray(),
                truncated,
                notes.ToArray());
        });

    [McpServerTool(Name = "assets", ReadOnly = true, Destructive = false),
     Description("Lists embedded assets (images, fonts). Returns URLs for open-asset.")]
    public Task<IReadOnlyList<AssetInfo>> Assets(CancellationToken cancellationToken = default) =>
        WithClientAsync(cancellationToken, (client, token) => client.Assets.GetAssetsAsync(token));

    [McpServerTool(Name = "open-asset", ReadOnly = true, Destructive = false),
     Description("Downloads asset by URL from assets tool.")]
    public Task<CallToolResult> OpenAsset(string assetUrl, CancellationToken cancellationToken = default) =>
        WithClientAsync(cancellationToken,
            async (client, token) =>
            {
                // The export protocol verifies the entire stream independently from the bounded preview API.
                using var data = new BoundedAssetStream(AssetDownloadLimit);
                var result = await client.Assets.ExportAssetAsync(assetUrl, data, cancellationToken: token)
                    .ConfigureAwait(false);
                var mediaType = AssetMediaType(assetUrl);
                return new CallToolResult
                {
                    Content =
                    [
                        new TextContentBlock
                        {
                            Text = JsonSerializer.Serialize(new
                            {
                                result.Uri, MediaType = mediaType, result.ByteLength, result.Sha256,
                                Complete = true, MaximumBytes = AssetDownloadLimit
                            })
                        },
                        new EmbeddedResourceBlock
                        {
                            Resource = BlobResourceContents.FromBytes(data.ToArray(), assetUrl, mediaType)
                        }
                    ]
                };
            });

    private ResourceProviderInfo PublicResourceProvider(ResourceProviderInfo provider) => provider with
    {
        Id = PublicNodeId(provider.Id), Children = provider.Children.Select(PublicResourceProvider).ToArray()
    };

    private static IEnumerable<ResourceProviderInfo> FlattenResourceProviders(IEnumerable<ResourceProviderInfo> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in FlattenResourceProviders(root.Children))
                yield return child;
        }
    }

    private static string AssetMediaType(string uri) =>
        Path.GetExtension(new Uri(uri).AbsolutePath).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
            ".gif" => "image/gif", ".bmp" => "image/bmp", ".svg" => "image/svg+xml", ".json" => "application/json",
            ".axaml" or ".xaml" or ".xml" => "application/xml", ".txt" or ".md" or ".css" or ".csv" => "text/plain",
            ".ttf" => "font/ttf", ".otf" => "font/otf", ".woff" => "font/woff", ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };

    private sealed class BoundedAssetStream(int maximumBytes) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Length + buffer.Length > maximumBytes)
                throw new McpException(
                    $"asset_download_limit: MCP embedded asset downloads are limited to {maximumBytes} bytes; the asset was not returned as a partial download.");
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
