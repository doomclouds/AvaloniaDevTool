using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;

namespace AvaloniaDevTool.Cli;

/// <summary>Owns the managed call gate and public node identities for one tool session.</summary>
public sealed class ManagedToolContext
{
    private const long NativeMask = uint.MaxValue;
    private readonly ToolSession _session;
    private readonly NodeGenerationAllocator _generations;
    private readonly SemaphoreSlim _calls = new(1, 1);
    private SessionInfo? _nodeSession;
    private long _nodeEpoch;
    private long _nodeContentGeneration;

    public ManagedToolContext(ToolSession session, NodeGenerationAllocator? generations = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _generations = generations ?? new NodeGenerationAllocator();
    }

    internal ToolSession OwnerSession => _session;

    public async Task<T> WithSessionAsync<T>(CancellationToken token,
        Func<ToolSession, CancellationToken, Task<T>> action)
    {
        await _calls.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var result = await action(_session, token).ConfigureAwait(false);
            SynchronizeNodeSession(_session.CurrentSession, _session.CurrentContentGeneration);
            return result;
        }
        catch (RpcException error)
        {
            throw new McpException($"{error.Code}: {error.Message}");
        }
        catch (ArgumentException error)
        {
            throw new McpException($"invalid_argument: {error.Message}");
        }
        finally
        {
            _calls.Release();
        }
    }

    public Task<T> WithClientAsync<T>(CancellationToken token,
        Func<DiagnosticsClient, CancellationToken, Task<T>> action) =>
        WithSessionAsync(token,
            async (owner, cancellation) =>
            {
                var client = await owner.GetAsync(cancellation).ConfigureAwait(false);
                var identity = client.Session;
                var generation = await client.RefreshContentGenerationAsync(cancellation).ConfigureAwait(false);
                SynchronizeNodeSession(identity, generation);
                var result = await action(client, cancellation).ConfigureAwait(false);
                var completedGeneration =
                    await client.RefreshContentGenerationAsync(cancellation).ConfigureAwait(false);
                if (!ReferenceEquals(identity, client.Session) || generation != completedGeneration)
                    throw new McpException("stale_session: The application connection changed during the operation.");
                return result;
            });

    private void SynchronizeNodeSession(SessionInfo? identity, long generation)
    {
        if (ReferenceEquals(identity, _nodeSession) && generation == _nodeContentGeneration)
            return;
        // Retire the previous identity even when the host generation budget is exhausted.
        _nodeSession = null;
        if (identity is not null)
        {
            _nodeEpoch = _generations.Allocate();
            _nodeSession = identity;
        }

        _nodeContentGeneration = generation;
    }

    // Node conversion is performed inside WithSessionAsync or WithClientAsync's managed gate.
    public long NativeNodeId(long nodeId)
    {
        if (nodeId == 0)
            return 0;
        if (nodeId < 0 || _nodeSession is null || (nodeId >> 32) != _nodeEpoch)
            throw new McpException("stale_node: Read tree or search again for this application session.");
        return nodeId & NativeMask;
    }

    public long PublicNodeId(long nativeId)
    {
        if (nativeId == 0)
            return 0;
        if (nativeId < 0 || nativeId > NativeMask || _nodeSession is null)
            throw new McpException("invalid_node: The target returned an unsupported object identity.");
        return (_nodeEpoch << 32) | nativeId;
    }

    public long? PublicNodeId(long? nativeId) => nativeId is { } value ? PublicNodeId(value) : null;

    public TreeNodeInfo PublicNode(TreeNodeInfo node) => node with
    {
        Id = PublicNodeId(node.Id), TemplateOwnerId = PublicNodeId(node.TemplateOwnerId),
        SlotContentId = PublicNodeId(node.SlotContentId)
    };

    public IReadOnlyList<TreeNodeInfo> PublicNodes(IEnumerable<TreeNodeInfo> nodes) =>
        nodes.Select(PublicNode).ToArray();
}
