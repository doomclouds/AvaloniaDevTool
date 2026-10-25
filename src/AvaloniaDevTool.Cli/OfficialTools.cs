using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

/// <summary>The official-compatible public surface; extension tools are registered separately.</summary>
[McpServerToolType]
public sealed partial class OfficialTools(ToolSession session)
{
    private readonly SemaphoreSlim _calls = new(1, 1);
    private SessionInfo? _nodeSession;
    private long _nodeEpoch;
    private long _nodeContentGeneration;
    private const long NativeMask = uint.MaxValue;
    internal static readonly HashSet<string> ToolNames =
    [
        "attach-to-app", "attach-to-file", "detach", "tree", "ancestors", "search", "screenshot", "props",
        "set-prop", "styles", "pseudo-class", "resources", "assets", "open-asset", "input", "action"
    ];

    private async Task<T> WithSessionAsync<T>(CancellationToken token, Func<ToolSession, CancellationToken, Task<T>> action)
    {
        await _calls.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var result = await action(session, token).ConfigureAwait(false);
            SynchronizeNodeSession(session.CurrentSession, session.CurrentContentGeneration);
            return result;
        }
        catch (RpcException error) { throw new McpException($"{error.Code}: {error.Message}"); }
        catch (ArgumentException error) { throw new McpException($"invalid_argument: {error.Message}"); }
        finally { _calls.Release(); }
    }

    private Task<T> WithClientAsync<T>(CancellationToken token, Func<DiagnosticsClient, CancellationToken, Task<T>> action) =>
        WithSessionAsync(token, async (owner, cancellation) =>
        {
            var client = await owner.GetAsync(cancellation).ConfigureAwait(false);
            var identity = client.Session;
            var generation = await client.RefreshContentGenerationAsync(cancellation).ConfigureAwait(false);
            SynchronizeNodeSession(identity, generation);
            var result = await action(client, cancellation).ConfigureAwait(false);
            var completedGeneration = await client.RefreshContentGenerationAsync(cancellation).ConfigureAwait(false);
            if (!ReferenceEquals(identity, client.Session) || generation != completedGeneration)
                throw new McpException("stale_session: The application connection changed during the operation.");
            return result;
        });

    internal Task<T> WithAgentClientAsync<T>(CancellationToken token, Func<DiagnosticsClient, CancellationToken, Task<T>> action) =>
        WithClientAsync(token, action);

    internal long AgentNativeNodeId(long nodeId) => NativeNodeId(nodeId);

    private void SynchronizeNodeSession(SessionInfo? identity, long generation)
    {
        if (ReferenceEquals(identity, _nodeSession) && generation == _nodeContentGeneration) return;
        _nodeSession = identity;
        _nodeContentGeneration = generation;
        if (identity is not null && ++_nodeEpoch > 0xFFFFF)
            throw new McpException("session_limit: Restart the MCP server before attaching another application.");
    }

    private long NativeNodeId(long nodeId)
    {
        if (nodeId == 0) return 0;
        if (nodeId < 0 || _nodeSession is null || (nodeId >> 32) != _nodeEpoch)
            throw new McpException("stale_node: Read tree or search again for this application session.");
        return nodeId & NativeMask;
    }
    private long PublicNodeId(long nativeId)
    {
        if (nativeId == 0) return 0;
        if (nativeId < 0 || nativeId > NativeMask || _nodeSession is null)
            throw new McpException("invalid_node: The target returned an unsupported object identity.");
        return (_nodeEpoch << 32) | nativeId;
    }
    private long? PublicNodeId(long? nativeId) => nativeId is { } value ? PublicNodeId(value) : null;
    private TreeNodeInfo PublicNode(TreeNodeInfo node) => node with
    {
        Id = PublicNodeId(node.Id), TemplateOwnerId = PublicNodeId(node.TemplateOwnerId), SlotContentId = PublicNodeId(node.SlotContentId)
    };
    private IReadOnlyList<TreeNodeInfo> PublicNodes(IEnumerable<TreeNodeInfo> nodes) => nodes.Select(PublicNode).ToArray();
}
