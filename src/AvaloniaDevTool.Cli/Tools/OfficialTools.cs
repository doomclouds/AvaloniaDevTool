using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

/// <summary>The official-compatible public surface; extension tools are registered separately.</summary>
[McpServerToolType]
public sealed partial class OfficialTools
{
    public OfficialTools(ToolSession session) : this(session, new ManagedToolContext(session))
    {
    }

    public OfficialTools(ToolSession session, ManagedToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(session, context.OwnerSession))
            throw new ArgumentException("The managed context must belong to the supplied tool session.",
                nameof(context));
        ManagedContext = context;
    }

    internal ManagedToolContext ManagedContext { get; }

    internal static readonly HashSet<string> ToolNames =
    [
        "attach-to-app", "attach-to-file", "detach", "tree", "ancestors", "search", "screenshot", "props",
        "set-prop", "styles", "pseudo-class", "resources", "assets", "open-asset", "input", "action"
    ];

    private Task<T> WithSessionAsync<T>(CancellationToken token,
        Func<ToolSession, CancellationToken, Task<T>> action) =>
        ManagedContext.WithSessionAsync(token, action);

    private Task<T> WithClientAsync<T>(CancellationToken token,
        Func<DiagnosticsClient, CancellationToken, Task<T>> action) =>
        ManagedContext.WithClientAsync(token, action);

    private long NativeNodeId(long nodeId) => ManagedContext.NativeNodeId(nodeId);
    private long PublicNodeId(long nativeId) => ManagedContext.PublicNodeId(nativeId);
    private long? PublicNodeId(long? nativeId) => ManagedContext.PublicNodeId(nativeId);
    private TreeNodeInfo PublicNode(TreeNodeInfo node) => ManagedContext.PublicNode(node);

    private IReadOnlyList<TreeNodeInfo> PublicNodes(IEnumerable<TreeNodeInfo> nodes) =>
        ManagedContext.PublicNodes(nodes);
}
