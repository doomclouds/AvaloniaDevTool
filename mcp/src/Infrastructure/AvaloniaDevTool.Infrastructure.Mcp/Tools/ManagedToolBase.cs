using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

/// <summary>Shared managed execution and target object identity for capability adapters.</summary>
[McpServerToolType]
public abstract class ManagedToolBase
{
    protected ManagedToolBase(ToolSession session) : this(session, new ManagedToolContext(session))
    {
    }

    protected ManagedToolBase(ToolSession session, ManagedToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(session, context.OwnerSession))
            throw new ArgumentException("The managed context must belong to the supplied tool session.",
                nameof(context));
        ManagedContext = context;
    }

    internal ManagedToolContext ManagedContext { get; }

    protected Task<T> WithSessionAsync<T>(CancellationToken token,
        Func<ToolSession, CancellationToken, Task<T>> action) =>
        ManagedContext.WithSessionAsync(token, action);

    protected Task<T> WithClientAsync<T>(CancellationToken token,
        Func<DiagnosticsClient, CancellationToken, Task<T>> action) =>
        ManagedContext.WithClientAsync(token, action);

    protected long NativeNodeId(long nodeId) => ManagedContext.NativeNodeId(nodeId);
    protected long PublicNodeId(long nativeId) => ManagedContext.PublicNodeId(nativeId);
    protected long? PublicNodeId(long? nativeId) => ManagedContext.PublicNodeId(nativeId);
    protected TreeNodeInfo PublicNode(TreeNodeInfo node) => ManagedContext.PublicNode(node);

    protected IReadOnlyList<TreeNodeInfo> PublicNodes(IEnumerable<TreeNodeInfo> nodes) =>
        ManagedContext.PublicNodes(nodes);
}
