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
public sealed class ObjectInspectionTools(ToolSession session) : TargetToolBase(session)
{
    [McpServerTool(Name = "tree-children", ReadOnly = true),
     Description(
         "Read a real direct-child page in Visual, Logical or Merged. count=1..2000 (default 100), offset=0..20000. Return totalCount, hasMore and nextCursor; passing nextCursor continues its encoded offset. Cursors are session/parent/kind scoped and fail with cursor_invalidated after branch changes. Branches over 20000 fail explicitly with tree_limit. Reading starts branch observation; tree-release releases it.")]
    public async Task<TreeChildrenPage> TreeChildren(long nodeId,
        TreeKind kind = TreeKind.Logical,
        int offset = 0,
        int count = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        await WithTreeErrorsAsync(async () =>
            await (await session.GetAsync(cancellationToken)).Tree.GetChildrenPageAsync(nodeId,
                kind,
                offset,
                count,
                cursor,
                cancellationToken));

    [McpServerTool(Name = "template-parent-selector", ReadOnly = true),
     Description(
         "Independently generate and validate the actual template parent's selector. Returns templateParentId and supported/match/uniqueness metadata; the part selector remains available through element-selector. Custom types without a namespace mapping and absent template parents are explicitly unsupported.")]
    public async Task<TemplateParentSelectorResult> TemplateParentSelector(long nodeId,
        CancellationToken cancellationToken) =>
        await WithTreeErrorsAsync(async () =>
            await (await session.GetAsync(cancellationToken))
                .Tree.GetTemplateParentSelectorAsync(nodeId, cancellationToken));

    private static async Task<T> WithTreeErrorsAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (RpcException exception)
        {
            throw new McpException(exception.Code + ": " + exception.Message);
        }
        catch (ArgumentException exception)
        {
            throw new McpException("invalid_request: " + exception.Message);
        }
    }

    [McpServerTool(Name = "element-selector", ReadOnly = true),
     Description(
         "Generate an Avalonia selector from actual element metadata and validate matches. Ambiguity, truncated validation and unsupported names are explicit; uniqueness is never guessed.")]
    public async Task<ElementSelectorResult> ElementSelector(long nodeId, CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).Tree.GetElementSelectorAsync(nodeId, cancellationToken);

    [McpServerTool(Name = "element-metadata", ReadOnly = true),
     Description(
         "Read session-scoped weak object identity, type/name/classes and validated selector for copying element metadata.")]
    public async Task<ElementMetadata> ElementMetadata(long nodeId, CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).Tree.GetElementMetadataAsync(nodeId, cancellationToken);

    [McpServerTool(Name = "tree-release"),
     Description(
         "Release expanded-branch change observations for the supplied object IDs and tree kind. Object identities and property/breakpoint observers are preserved; reading children again restores tree observation.")]
    public async Task<TreeObservationResult> ReleaseTree(long[] nodeIds,
        TreeKind kind = TreeKind.Visual,
        CancellationToken cancellationToken = default) =>
        await (await session.GetAsync(cancellationToken)).Tree.ReleaseTreeAsync(nodeIds, kind, cancellationToken);

    [McpServerTool(Name = "object", ReadOnly = true),
     Description(
         "Read public CLR properties and fields, or bounded IList/IDictionary items. Complex values have weak object IDs for explicit drill-in; getters run on the target UI thread and per-member failures are returned.")]
    public async Task<object> InspectObject(long objectId,
        bool items = false,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        return items
            ? await client.Runtime.GetRuntimeItemsAsync(objectId, offset, limit, cancellationToken)
            : await client.Runtime.GetRuntimeMembersAsync(objectId, offset, limit, cancellationToken);
    }
}
