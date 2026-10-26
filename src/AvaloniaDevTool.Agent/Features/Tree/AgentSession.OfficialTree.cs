using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private const int OfficialTraversalLimit = 100000;

    private OfficialTreeSnapshot GetOfficialTree(OfficialTreeRequest request)
    {
        ValidateTreeKind(request.Kind);
        if (request.Depth < -1)
            throw new RpcException("invalid_request", "Depth must be -1 or non-negative.");
        var roots = RefreshRoots();
        IReadOnlyList<object> start;
        long? parentId = null;
        if (request.ObjectId == 0)
            start = roots.Cast<object>().ToArray();
        else
        {
            var parent = _objects.Get(request.ObjectId);
            OfficialPath(parent, request.Kind, roots);
            start = ChildObjects(parent, request.Kind).ToArray();
            parentId = request.ObjectId;
        }

        if (request.Depth == 0)
            return new OfficialTreeSnapshot([], 0);

        var result = new List<OfficialTreeEntry>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<(object Value, long? ParentId, int Depth)>();
        for (var index = start.Count - 1; index >= 0; index--)
            pending.Push((start[index], parentId, 0));
        while (pending.TryPop(out var entry))
        {
            if (!seen.Add(entry.Value))
                continue;
            CheckOfficialTraversalLimit(seen.Count);
            var node = Describe(entry.Value, request.Kind);
            result.Add(new OfficialTreeEntry(node, entry.ParentId, entry.Depth));
            if (request.Depth != -1 && entry.Depth + 1 >= request.Depth)
                continue;
            var children = ChildObjects(entry.Value, request.Kind).ToArray();
            for (var index = children.Length - 1; index >= 0; index--)
                pending.Push((children[index], node.Id, entry.Depth + 1));
        }

        return new OfficialTreeSnapshot(result, result.Count);
    }

    private IReadOnlyList<TreeNodeInfo> GetOfficialAncestors(long objectId)
    {
        var target = _objects.Get(objectId);
        return OfficialPath(target, TreeKind.Merged, RefreshRoots()).Select(item => Describe(item, TreeKind.Merged))
            .ToArray();
    }

    private TreeSearchResult SearchOfficialTree(OfficialTreeSearchRequest request)
    {
        if (request.Query is null)
            throw new RpcException("invalid_request", "Query must be a string.");
        if (request.Limit < -1)
            throw new RpcException("invalid_request", "Limit must be -1 or non-negative.");
        var roots = RefreshRoots();
        var kind = request.SearchTemplates ? TreeKind.Merged : TreeKind.Logical;
        var pending = new Queue<object>();
        var scheduled = new HashSet<object>(ReferenceEqualityComparer.Instance);

        void Schedule(object item)
        {
            if (!scheduled.Add(item))
                return;
            CheckOfficialTraversalLimit(scheduled.Count);
            pending.Enqueue(item);
        }

        if (request.ScopeObjectId == 0)
            foreach (var root in roots)
                Schedule(root);
        else
        {
            var scope = _objects.Get(request.ScopeObjectId);
            OfficialPath(scope, TreeKind.Merged, roots);
            Schedule(scope);
        }

        if (request.Limit == 0)
            return new TreeSearchResult([], 0, false);

        var matches = new List<TreeNodeInfo>();
        var visited = 0;
        while (pending.TryDequeue(out var current))
        {
            visited++;
            if (OfficialMatches(current, request.Query))
            {
                // Read one extra match to report truncation accurately at the requested result boundary.
                if (request.Limit != -1 && matches.Count == request.Limit)
                    return new TreeSearchResult(matches, visited, true);
                matches.Add(Describe(current, kind));
            }

            foreach (var child in ChildObjects(current, kind))
                Schedule(child);
        }

        return new TreeSearchResult(matches, visited, false);
    }

    private static bool OfficialMatches(object value, string query) => value is not MergedTree.SyntheticNode &&
                                                                       ((value.GetType().FullName ??
                                                                         value.GetType().Name).Contains(query,
                                                                            StringComparison.OrdinalIgnoreCase) ||
                                                                        value is StyledElement { Name: { } name } &&
                                                                        name.Contains(query,
                                                                            StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<object> OfficialPath(object target, TreeKind kind, IReadOnlyList<TopLevel> roots)
    {
        var path = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object? current = target;
        while (current is not null)
        {
            if (!seen.Add(current))
                throw new RpcException("invalid_tree", "The ancestor chain contains a cycle.");
            CheckOfficialTraversalLimit(path.Count + 1);
            if (current is MergedTree.SyntheticNode synthetic)
                synthetic.Validate();
            path.Add(current);
            if (roots.Any(root => ReferenceEquals(root, current)))
            {
                path.Reverse();
                return path;
            }

            current = kind switch
            {
                TreeKind.Merged => _mergedTree.Parent(current),
                TreeKind.Visual when current is Visual visual => visual.GetVisualParent(),
                TreeKind.Logical when current is ILogical logical => logical.LogicalParent,
                _ => null
            };
        }

        throw new RpcException("object_detached", "The object is not reachable from a current root in this tree.");
    }

    private static void CheckOfficialTraversalLimit(int count)
    {
        if (count > OfficialTraversalLimit)
            throw new RpcException("tree_limit",
                "The operation exceeds the explicit 100000-node traversal safety limit. Narrow its scope.");
    }
}
