using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "screenshot", ReadOnly = true), Description("Capture a target control as PNG. Maximum edge 2048, 2 million pixels, encoded PNG up to 1 MiB; oversized captures fail explicitly.")]
    public async Task<ImageContentBlock> Screenshot(long nodeId, CancellationToken cancellationToken)
    {
        var capture = await (await session.GetAsync(cancellationToken)).CaptureElementAsync(nodeId, cancellationToken);
        return ImageContentBlock.FromBytes(Convert.FromBase64String(capture.PngBase64), "image/png");
    }

    [McpServerTool(Name = "resources", ReadOnly = true), Description("Read application/style resources and an optional selected node's local ancestor scopes. Supply dictionaryId to read entries.")]
    public async Task<object> Resources(long? nodeId = null, long? dictionaryId = null, CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        return dictionaryId is { } id ? await client.GetResourceEntriesAsync(id, cancellationToken)
            : await client.GetResourcesAsync(nodeId, cancellationToken);
    }

    [McpServerTool(Name = "set-resource"), Description("Temporarily edit an existing string-key resource. On detach it is restored only if the application has not replaced the edited value.")]
    public async Task<string> SetResource(long dictionaryId, string key, string value, CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).SetResourceAsync(dictionaryId, key, value, cancellationToken);
        return "Resource edit applied";
    }

    [McpServerTool(Name = "reset-resource"), Description("Restore this session's original resource value, preserving a newer application write on conflict.")]
    public async Task<string> ResetResource(long dictionaryId, string key, CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).ResetResourceAsync(dictionaryId, key, cancellationToken);
        return "Resource edit reset";
    }

    [McpServerTool(Name = "assets", ReadOnly = true), Description("List up to 2000 avares assets from loaded target assemblies.")]
    public async Task<IReadOnlyList<AssetInfo>> Assets(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).GetAssetsAsync(cancellationToken);

    [McpServerTool(Name = "open-asset", ReadOnly = true), Description("Preview one avares asset. Text is bounded at 1 MiB; supported small images return native MCP image content.")]
    public async Task<CallToolResult> OpenAsset(string assetUrl, CancellationToken cancellationToken)
    {
        var result = await (await session.GetAsync(cancellationToken)).OpenAssetAsync(assetUrl, cancellationToken);
        List<ContentBlock> content = [new TextContentBlock { Text = System.Text.Json.JsonSerializer.Serialize(result with { Base64 = null }, ProtocolJson.Options) }];
        if (result.Base64 is { } image)
            content.Add(ImageContentBlock.FromBytes(Convert.FromBase64String(image), result.MediaType));
        return new CallToolResult { Content = content };
    }
}
