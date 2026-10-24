using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Cli;

[McpServerToolType]
public sealed partial class InspectorTools(ToolSession session)
{
    [McpServerTool(Name = "apps", ReadOnly = true), Description("List discoverable local AvaloniaDevTool pipe names on Windows. Other platforms accept a manually supplied pipe name.")]
    public IReadOnlyList<string> Apps() => OperatingSystem.IsWindows()
        ? Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>()
            .Where(name => name.StartsWith(DiagnosticsProtocol.PipePrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        : [];

    [McpServerTool(Name = "attach-to-app", ReadOnly = true), Description("Connect this MCP session to an application using its AvaloniaDevTool pipe name. The current Agent supports one inspector connection at a time.")]
    public Task<SessionInfo> AttachToApp(string pipeName, CancellationToken cancellationToken) => session.AttachAsync(pipeName, cancellationToken);

    [McpServerTool(Name = "detach"), Description("Disconnect and release this session's temporary property, resource and pseudo-class edits.")]
    public async Task<string> Detach() { await session.DetachAsync(); return "Disconnected"; }

    [McpServerTool(Name = "tree", ReadOnly = true), Description("Read Visual, Logical or Merged tree snapshots. includeTemplates=false stops recursion at Merged template groups. depth=0 reads roots, -1 recurses within maxNodes (1..2000) and 64 levels. Optional offset/count/cursor select direct-child paging instead: require nodeId and depth=1, count=1..2000 (default 100); maxNodes and includeTemplates apply only to recursive mode. Page metadata exposes total count, continuation and hasMore. Changed branches reject stale cursors explicitly.")]
    public async Task<ToolTreeSnapshot> Tree(long? nodeId = null, TreeKind kind = TreeKind.Logical, int depth = 1, int maxNodes = 500, bool includeTemplates = true, CancellationToken cancellationToken = default,
        int? offset = null, int? count = null, string? cursor = null)
        => await WithTreeErrorsAsync(() => ReadTreeAsync(nodeId, kind, depth, maxNodes, includeTemplates, cancellationToken, offset, count, cursor));

    private async Task<ToolTreeSnapshot> ReadTreeAsync(long? nodeId, TreeKind kind, int depth, int maxNodes, bool includeTemplates,
        CancellationToken cancellationToken, int? offset, int? count, string? cursor)
    {
        if (depth < -1 || depth > 64 || maxNodes is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(depth), "depth must be -1..64 and maxNodes 1..2000.");
        var client = await session.GetAsync(cancellationToken);
        var roots = nodeId is { } id ? await NodeAsRoot(client, id, kind, cancellationToken) : await client.GetRootsAsync(kind, cancellationToken);
        if (offset is not null || count is not null || cursor is not null)
        {
            if (nodeId is not { } parentId || depth != 1)
                throw new ArgumentException("Direct-child paging requires nodeId and depth=1.");
            var page = await client.GetChildrenPageAsync(parentId, kind, offset ?? 0, count ?? 100, cursor, cancellationToken);
            return new([new(roots.Single(), page.Children.Select(child => new ToolTreeNode(child, [])).ToArray())],
                page.Children.Count + 1, page.HasMore, page);
        }
        var visited = new HashSet<long>();
        var truncated = false;
        async Task<ToolTreeNode?> Visit(TreeNodeInfo node, int remaining, int level)
        {
            if (visited.Count >= maxNodes) { truncated = true; return null; }
            if (!visited.Add(node.Id)) return null;
            var children = new List<ToolTreeNode>();
            if (node.HasChildren && remaining != 0 && (includeTemplates || node.Role != TreeNodeRole.TemplateGroup))
            {
                if (level >= 64) truncated = true;
                else
                {
                    string? continuation = null;
                    do
                    {
                        if (visited.Count >= maxNodes) { truncated = true; break; }
                        var page = await client.GetChildrenPageAsync(node.Id, kind, count: Math.Min(100, maxNodes - visited.Count), cursor: continuation, cancellationToken: cancellationToken);
                        foreach (var child in page.Children)
                        {
                            var result = await Visit(child, remaining < 0 ? -1 : remaining - 1, level + 1);
                            if (result is not null) children.Add(result);
                        }
                        continuation = page.NextCursor;
                    } while (continuation is not null);
                }
            }
            return new ToolTreeNode(node, children);
        }
        var items = new List<ToolTreeNode>();
        foreach (var root in roots)
        {
            var result = await Visit(root, depth, 0);
            if (result is not null) items.Add(result);
        }
        return new ToolTreeSnapshot(items, visited.Count, truncated);
    }

    private static async Task<IReadOnlyList<TreeNodeInfo>> NodeAsRoot(DiagnosticsClient client, long id, TreeKind kind, CancellationToken token)
    {
        var path = await client.GetAncestorsAsync(id, kind, token);
        return [path.Last()];
    }

    [McpServerTool(Name = "props", ReadOnly = true), Description("Read properties with stable owner-qualified IDs, effective values, priorities and editability. contextual filters only known direct-parent layout properties; unknown attached members remain. includeClr explicitly opts into read-only public CLR getters. Both options default false for compatibility.")]
    public async Task<IReadOnlyList<PropertyInfoDto>> Props(long nodeId, string[]? propertyNames = null,
        bool contextual = false, bool includeClr = false, CancellationToken cancellationToken = default)
    {
        var values = await (await session.GetAsync(cancellationToken)).GetPropertiesAsync(new PropertiesRequest(nodeId, includeClr, contextual), cancellationToken);
        return propertyNames is null ? values : values.Where(value => propertyNames.Contains(value.Name, StringComparer.Ordinal)
            || propertyNames.Contains(value.Id, StringComparer.Ordinal)).ToArray();
    }

    [McpServerTool(Name = "set-prop"), Description("Apply a reversible diagnostic property override. Use the owner-qualified property ID to avoid ambiguous names. null clears this session's override. Existing bindings are preserved.")]
    public async Task<string> SetProperty(long nodeId, string propertyName, string? value, CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        var properties = await client.GetPropertiesAsync(nodeId, cancellationToken);
        var candidates = properties.Where(property => property.Id == propertyName || property.Name == propertyName).ToArray();
        if (candidates.Length != 1)
            throw new RpcException("ambiguous_property", "Supply one exact property ID from props.");
        if (value is null) await client.ClearPropertyAsync(nodeId, candidates[0].Id, cancellationToken);
        else await client.SetPropertyAsync(nodeId, candidates[0].Id, value, cancellationToken);
        return value is null ? "Override cleared" : "Diagnostic override applied";
    }

    [McpServerTool(Name = "styles", ReadOnly = true), Description("Inspect Avalonia 12 style frames, setters, binding expression descriptions and errors. Value equality is not proof that a setter uniquely wins.")]
    public async Task<StyleSnapshot> Styles(long nodeId, CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).GetStylesAsync(nodeId, cancellationToken);

    [McpServerTool(Name = "ancestors", ReadOnly = true), Description("Return the root-to-node path in the selected tree, including the target node.")]
    public async Task<IReadOnlyList<TreeNodeInfo>> Ancestors(long nodeId, TreeKind kind = TreeKind.Logical, CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).GetAncestorsAsync(nodeId, kind, cancellationToken);

    [McpServerTool(Name = "search", ReadOnly = true), Description("Search node type, name and preview text with explicit result and traversal bounds.")]
    public async Task<TreeSearchResult> Search(string query, TreeKind kind = TreeKind.Logical, long? rootId = null, int maxResults = 100,
        int maxVisited = 3000, CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).SearchTreeAsync(new TreeSearchRequest(query, kind, rootId, maxResults, maxVisited), cancellationToken);

    [McpServerTool(Name = "action"), Description("Focus, BringIntoView, Enable or Disable a target control. Enable/Disable use reversible diagnostic overrides.")]
    public async Task<ElementActionResult> Action(long nodeId, ElementAction action, CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).PerformElementActionAsync(nodeId, action, cancellationToken);

    [McpServerTool(Name = "follow-focus"), Description("Follow keyboard focus in the target application through selection notifications. Disabling releases the mode for this session.")]
    public async Task<string> FollowFocus(bool enabled, CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).SetFollowFocusAsync(enabled, cancellationToken);
        return enabled ? "Focus following enabled" : "Focus following disabled";
    }

    [McpServerTool(Name = "pseudo-class"), Description("List pseudo-classes, temporarily set one, or reset this session's modification. Real input/state changes may recalculate pseudo-classes.")]
    public async Task<IReadOnlyList<PseudoClassInfo>> PseudoClass(long nodeId, string? name = null, bool? enabled = null, bool reset = false,
        CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        if (name is not null)
        {
            if (reset) await client.ResetPseudoClassAsync(nodeId, name, cancellationToken);
            else if (enabled is { } value) await client.SetPseudoClassAsync(nodeId, name, value, cancellationToken);
        }
        return await client.GetPseudoClassesAsync(nodeId, cancellationToken);
    }

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

    [McpServerTool(Name = "activity-start"), Description("Capture bounded Avalonia logs and/or routed events. Event filter defaults to Click; '*' enables all registered routed events.")]
    public async Task<ActivityStatus> StartActivity(bool logs = true, bool events = false, long? sourceNodeId = null, string eventName = "Click",
        string minimumLevel = "Warning", string? categoryFilter = null, string? messageFilter = null,
        string? parameterName = null, string? parameterValue = null, CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        await client.StartActivityAsync(new ActivityStartRequest(logs, events, sourceNodeId, eventName, minimumLevel,
            new LogFilter(categoryFilter, messageFilter, parameterName, parameterValue)), cancellationToken);
        return await client.GetActivityStatusAsync(cancellationToken);
    }

    [McpServerTool(Name = "activity-stop"), Description("Stop log/event capture and release listeners, retaining the bounded history.")]
    public async Task<ActivityStatus> StopActivity(CancellationToken cancellationToken)
    {
        var client = await session.GetAsync(cancellationToken);
        await client.StopActivityAsync(cancellationToken);
        return await client.GetActivityStatusAsync(cancellationToken);
    }

    [McpServerTool(Name = "activity", ReadOnly = true), Description("Read up to 500 captured log/event entries, including route, handled state and source object IDs.")]
    public async Task<IReadOnlyList<ActivityEntry>> Activity(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).GetActivityAsync(cancellationToken);
}

public sealed record ToolTreeNode(TreeNodeInfo Node, IReadOnlyList<ToolTreeNode> Children);
public sealed record ToolTreeSnapshot(IReadOnlyList<ToolTreeNode> Roots, int Visited, bool Truncated, TreeChildrenPage? Page = null);

