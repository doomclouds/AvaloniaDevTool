using AvaloniaDevTool.Protocol;
using Upstream = global::XamlMcp.Protocol;

namespace AvaloniaDevTool.Backends.XamlMcp;

/// <summary>Maps a complete bounded upstream snapshot into non-reused local handles.</summary>
internal sealed class XamlSnapshotCache
{
    private Dictionary<long, CachedNode> _nodes = [];
    private Dictionary<int, long> _nativeIds = [];
    private HashSet<long> _roots = [];
    private long _nextLocalId;
    public long? SnapshotId { get; private set; }
    public TreeKind? Kind { get; private set; }
    public bool Truncated { get; private set; }
    public int Count => _nodes.Count;

    public IReadOnlyList<TreeNodeInfo> Load(Upstream.TreeResult snapshot, TreeKind kind, int maximum)
    {
        if (snapshot.SnapshotId <= 0 || snapshot.Roots is null)
            throw new RpcException("invalid_response", "The upstream tree has no valid snapshot identity.");
        var nodes = new Dictionary<long, CachedNode>();
        var nativeIds = new Dictionary<int, long>();
        var roots = new HashSet<long>();
        var nextId = _nextLocalId;
        long Add(Upstream.TreeNodeDto node, long? parent, int depth)
        {
            if (nodes.Count >= maximum || depth > 24 || node.ChildCount < 0 || node.NodeId <= 0)
                throw new RpcException("invalid_response", "The upstream tree exceeded its requested bounds.");
            if (nativeIds.ContainsKey(node.NodeId))
                throw new RpcException("invalid_response", "A node identifier occurs twice in one upstream snapshot.");
            var local = checked(++nextId);
            nativeIds.Add(node.NodeId, local);
            var info = new TreeNodeInfo(local, node.Type, node.Name,
                node.KeyProps?.GetValueOrDefault("text"), node.ChildCount > 0);
            var cached = new CachedNode(info, snapshot.SnapshotId, node.NodeId, parent, node.ChildCount, []);
            nodes.Add(local, cached);
            cached.Children = (node.Children ?? []).Select(child => Add(child, local, depth + 1)).ToArray();
            if (cached.Children.Count > node.ChildCount)
                throw new RpcException("invalid_response", "The upstream child count is inconsistent.");
            return local;
        }
        foreach (var root in snapshot.Roots)
            roots.Add(Add(root, null, 0));
        _nodes = nodes;
        _nativeIds = nativeIds;
        _roots = roots;
        _nextLocalId = nextId;
        SnapshotId = snapshot.SnapshotId;
        Kind = kind;
        Truncated = snapshot.Truncated;
        return roots.Select(id => nodes[id].Info).ToArray();
    }

    public CachedNode Resolve(long id) => _nodes.GetValueOrDefault(id) ??
        throw new RpcException("snapshot_expired", "The node belongs to an earlier tree snapshot. Refresh and select it again.");

    public CachedNode ResolveNative(int nodeId) => _nativeIds.TryGetValue(nodeId, out var local) ? Resolve(local) :
        throw new RpcException("snapshot_incomplete", "The upstream ancestor is outside the retained local snapshot.");

    public IReadOnlyList<TreeNodeInfo> Children(long id, TreeKind kind)
    {
        RequireKind(kind);
        var node = Resolve(id);
        if (node.ChildCount > node.Children.Count)
            throw new RpcException("snapshot_truncated", "This branch was truncated. Increase the snapshot limit or refresh a narrower target.");
        return node.Children.Select(child => Resolve(child).Info).ToArray();
    }

    public IReadOnlyList<TreeNodeInfo> LocalAncestors(long id, TreeKind kind)
    {
        RequireKind(kind);
        var path = new List<TreeNodeInfo>();
        var node = Resolve(id);
        var visited = new HashSet<long>();
        while (true)
        {
            if (!visited.Add(node.Info.Id))
                throw new RpcException("invalid_response", "The cached ancestor path contains a cycle.");
            path.Add(node.Info);
            if (node.Parent is not { } parent)
            {
                if (!_roots.Contains(node.Info.Id))
                    throw new RpcException("not_supported", "The logical ancestor path is outside this bounded snapshot.");
                break;
            }
            node = Resolve(parent);
        }
        path.Reverse();
        return path;
    }

    public void RequireKind(TreeKind kind)
    {
        if (kind != Kind)
            throw new RpcException("tree_scope_mismatch", "Refresh the requested tree scope before using its nodes.");
    }

    public void Clear()
    {
        _nodes.Clear();
        _nativeIds.Clear();
        _roots.Clear();
        SnapshotId = null;
        Kind = null;
        Truncated = false;
    }

    internal sealed class CachedNode(TreeNodeInfo info, long snapshotId, int nativeNodeId,
        long? parent, int childCount, IReadOnlyList<long> children)
    {
        public TreeNodeInfo Info { get; } = info;
        public long SnapshotId { get; } = snapshotId;
        public int NativeNodeId { get; } = nativeNodeId;
        public long? Parent { get; } = parent;
        public int ChildCount { get; } = childCount;
        public IReadOnlyList<long> Children { get; set; } = children;
        public Upstream.TargetDto Target => new() { SnapshotId = SnapshotId, NodeId = NativeNodeId };
    }
}

