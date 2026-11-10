using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Specialized;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using System.Reflection;
using System.Text.Json;
using Avalonia.Controls.Metadata;
using Avalonia.Input;

namespace AvaloniaDevTool.Agent;

internal sealed class TreeInspectionService : IDisposable
{
    private readonly WeakObjectRegistry _objects;
    private readonly ObjectObservationService _observations;
    private readonly Func<List<TopLevel>> _refreshRoots;
    private readonly Action<string, object> _notify;
    private readonly string _sessionId;
    private string _rootsSnapshot = "";
    private bool _disposed;

    internal TreeInspectionService(WeakObjectRegistry objects,
        ObjectObservationService observations,
        Func<List<TopLevel>> refreshRoots,
        Action<string, object> notify,
        string sessionId)
    {
        _objects = objects;
        _observations = observations;
        _refreshRoots = refreshRoots;
        _notify = notify;
        _sessionId = sessionId;
        _observations.TemplateApplied += OnTemplateApplied;
    }

    private long Observe(object value) => _observations.Observe(value);
    private List<TopLevel> RefreshRoots() => _refreshRoots();
    private void Notify(string method, object payload) => _notify(method, payload);
    private string Fingerprint(IEnumerable<object> children) => string.Join(",", children.Select(_objects.GetId));
    internal void InitializeRoots() => _rootsSnapshot = Fingerprint(RefreshRoots());
    internal void RefreshRootObservations(IReadOnlyList<TopLevel> roots) => PruneTreeObservations(roots);

    internal void PollRoots()
    {
        var snapshot = Fingerprint(RefreshRoots());
        if (snapshot == _rootsSnapshot)
            return;
        _rootsSnapshot = snapshot;
        Notify(Methods.TreeInvalidated, new TreeInvalidatedNotification());
    }

    private void OnTemplateApplied(Avalonia.Controls.Primitives.TemplatedControl owner)
    {
        if (_disposed)
            return;
        _mergedTree.Invalidate(owner);
        var id = _objects.GetId(owner);
        if (_treeObservations.Keys.Any(key => key.Id == id))
            Notify(Methods.TreeInvalidated, new TreeInvalidatedNotification(id));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _observations.TemplateApplied -= OnTemplateApplied;
        DisposeTreeObservations();
    }

    private readonly MergedTree _mergedTree = new();

    internal void Register(RpcMethodRegistry routes)
    {
        routes.Map<TreeRootsRequest>(Methods.TreeRoots, request => GetRoots(request.Kind));
        routes.Map<TreeChildrenRequest>(Methods.TreeChildren, request => GetChildren(request));
        routes.Map<TreeChildrenPageRequest>(TreePagingMethods.Children, request => GetChildrenPage(request));
        routes.Map<ObjectRequest>(TreePagingMethods.TemplateParentSelector,
            request => GetTemplateParentSelector(request.ObjectId));
        routes.Map<TreeObserveRequest>(TreeWorkflowMethods.Observe, request => ObserveTree(request));
        routes.Map<TreeReleaseRequest>(TreeWorkflowMethods.Release, request => ReleaseTree(request));
        routes.Map<ObjectRequest>(TreeWorkflowMethods.Selector, request => GetElementSelector(request.ObjectId));
        routes.Map<ObjectRequest>(TreeWorkflowMethods.Metadata, request => GetElementMetadata(request.ObjectId));
        routes.Map<ControlTreeRequest>(ControlTreeMethods.Snapshot, GetOfficialTree);
        routes.Map<ObjectRequest>(ControlTreeMethods.Ancestors, request => GetOfficialAncestors(request.ObjectId));
        routes.Map<ControlSearchRequest>(ControlTreeMethods.Search, SearchOfficialTree);
        routes.Map<TreeAncestorsRequest>(ExplorationMethods.Ancestors, GetAncestors);
        routes.Map<TreeSearchRequest>(ExplorationMethods.Search, SearchTree);
    }

    private IReadOnlyList<TreeNodeInfo> GetRoots(TreeKind kind)
    {
        ValidateTreeKind(kind);
        return RefreshRoots().Select(x => Describe(x, kind)).ToArray();
    }

    private IReadOnlyList<TreeNodeInfo> GetChildren(TreeChildrenRequest request)
    {
        ValidateTreeKind(request.Kind);
        var parent = _objects.Get(request.ObjectId);
        if (request.Kind == TreeKind.Merged)
            MergedPath(parent, RefreshRoots());
        var children = ChildObjects(parent, request.Kind).ToArray();
        ObserveTree(new TreeObserveRequest(request.ObjectId, request.Kind));
        return children.Select(x => Describe(x, request.Kind)).ToArray();
    }

    private TreeNodeInfo Describe(object value, TreeKind kind)
    {
        var id = Observe(value);
        if (kind == TreeKind.Merged)
        {
            var metadata = _mergedTree.Metadata(value);
            if (value is MergedTree.SyntheticNode synthetic)
                return new TreeNodeInfo(id,
                    value.GetType().FullName!,
                    synthetic.Label,
                    null,
                    ChildObjects(value, kind).Any(),
                    metadata.Role,
                    metadata.Owner is { } owner ? Observe(owner) : null,
                    metadata.Content is { } content ? Observe(content) : null);
        }

        var name = (value as StyledElement)?.Name;
        var preview = value switch
        {
            TextBlock text => text.Text,
            TextBox text => text.Text,
            ContentControl { Content: string text } => text,
            Window window => window.Title,
            _ => null
        };
        if (preview?.Length > 100)
            preview = preview[..100] + "…";
        var details = kind == TreeKind.Merged ? _mergedTree.Metadata(value) : default;
        return new TreeNodeInfo(id,
            value.GetType().FullName ?? value.GetType().Name,
            name,
            preview,
            ChildObjects(value, kind).Any(),
            details.Role,
            details.Owner is { } templateOwner ? Observe(templateOwner) : null);
    }

    private IEnumerable<object> ChildObjects(object parent, TreeKind kind)
    {
        IEnumerable<object> children = kind switch
        {
            TreeKind.Visual when parent is Visual visual => visual.GetVisualChildren(),
            TreeKind.Logical when parent is ILogical logical => logical.LogicalChildren,
            TreeKind.Merged => _mergedTree.Children(parent),
            _ => []
        };
        return children.Where(x => !OverlayService.IsDiagnosticVisual(x));
    }

    private TreeChildrenPage GetChildrenPage(TreeChildrenPageRequest request)
    {
        ValidateTreeKind(request.Kind);
        if (request.Offset is < 0 or > 20000 || request.Count is < 1 or > 2000 || request.Cursor?.Length > 128)
            throw new RpcException("invalid_request",
                "Offset must be 0..20000, Count 1..2000 and Cursor at most 128 characters.");
        var parent = _objects.Get(request.ObjectId);
        // Validate attachment in every tree kind, including retained but detached real objects.
        GetAncestors(new TreeAncestorsRequest(request.ObjectId, request.Kind));
        var children = ChildObjects(parent, request.Kind).Take(20001).ToArray();
        if (children.Length > 20000)
            throw new RpcException("tree_limit",
                "A tree branch exceeds the 20000-child safety limit; no partial result was returned.");
        if (request.Cursor is not null && !_treeObservations.ContainsKey((request.ObjectId, request.Kind)))
            throw new RpcException("cursor_invalidated",
                "The branch observation was released or this cursor belongs to another tree. Restart at offset zero.");
        ObserveTree(new TreeObserveRequest(request.ObjectId, request.Kind));
        var observation = _treeObservations[(request.ObjectId, request.Kind)];
        var offset = request.Offset;
        var fingerprint = Fingerprint(children);

        string Cursor(int next) => next.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"{_sessionId}:{request.ObjectId}:{request.Kind}:{observation.CursorIdentity}:{observation.Revision}:{next}:{fingerprint}")));

        if (request.Cursor is { } cursor)
        {
            var separator = cursor.IndexOf(':');
            if (separator <= 0 ||
                !int.TryParse(cursor.AsSpan(0, separator),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out offset) || offset is < 0 or > 20000)
                throw new RpcException("invalid_cursor", "The tree continuation cursor is malformed.");
            if (request.Offset != 0 && request.Offset != offset)
                throw new RpcException("invalid_request", "Offset disagrees with the continuation cursor.");
            if (!string.Equals(cursor, Cursor(offset), StringComparison.Ordinal))
                throw new RpcException("cursor_invalidated",
                    "The tree branch changed or the cursor belongs to another session, parent or tree kind. Restart at offset zero.");
        }

        if (offset > children.Length)
            throw new RpcException("invalid_request", "Offset exceeds the current direct-child count.");
        var page = children.Skip(offset).Take(request.Count).Select(child => Describe(child, request.Kind)).ToArray();
        var next = offset + page.Length;
        var more = next < children.Length;
        return new(page, offset, children.Length, more, more ? Cursor(next) : null);
    }

    private TemplateParentSelectorResult GetTemplateParentSelector(long id)
    {
        var target = _objects.Get(id);
        if (target is MergedTree.SyntheticNode synthetic)
            synthetic.Validate();
        else if (target is Avalonia.Visual)
            GetAncestors(new TreeAncestorsRequest(id, TreeKind.Visual));
        var owner = target is MergedTree.SyntheticNode
            ? _mergedTree.Metadata(target).Owner
            : (target as StyledElement)?.TemplatedParent;
        if (owner is not StyledElement styled)
            return new(id, null, new(false, null, false, 0, false, "This node has no current template parent."));
        var ownerId = Observe(styled);
        return new(id, ownerId, ElementSelectorBuilder.Build(styled, RefreshRoots()));
    }

    private readonly Dictionary<(long Id, TreeKind Kind), TreeBranchObservation> _treeObservations = [];

    private ElementSelectorResult GetElementSelector(long id) => _objects.Get(id) is StyledElement element
        ? ElementSelectorBuilder.Build(element, RefreshRoots())
        : new(false, null, false, 0, false, "Synthetic nodes have no Avalonia selector.");

    private ElementMetadata GetElementMetadata(long id)
    {
        var target = _objects.Get(id);
        var styled = target as StyledElement;
        return new(_sessionId,
            "native",
            id,
            target.GetType().FullName ?? target.GetType().Name,
            styled?.Name,
            styled?.Classes.ToArray() ?? [],
            GetElementSelector(id));
    }

    private TreeObservationResult ObserveTree(TreeObserveRequest request)
    {
        ValidateTreeKind(request.Kind);
        var target = _objects.Get(request.ObjectId);
        if (request.Kind == TreeKind.Merged)
            MergedPath(target, RefreshRoots());
        var key = (request.ObjectId, request.Kind);
        if (!_treeObservations.ContainsKey(key))
            _treeObservations.Add(key, new TreeBranchObservation(this, target, request.ObjectId, request.Kind));
        return new(_treeObservations.Count);
    }

    private TreeObservationResult ReleaseTree(TreeReleaseRequest request)
    {
        ValidateTreeKind(request.Kind);
        if (request.ObjectIds is null || request.ObjectIds.Count > 20000)
            throw new RpcException("invalid_request", "ObjectIds must contain at most 20000 branch IDs.");
        foreach (var id in request.ObjectIds)
            if (_treeObservations.Remove((id, request.Kind), out var observation))
                observation.Dispose();
        return new(_treeObservations.Count);
    }

    private void TreeBranchChanged(long id, TreeKind kind)
    {
        if (!_disposed && _treeObservations.ContainsKey((id, kind)))
            Notify(Methods.TreeInvalidated, new TreeInvalidatedNotification(id));
    }

    private void PruneTreeObservations(IReadOnlyList<TopLevel> roots)
    {
        foreach (var pair in _treeObservations.ToArray())
            if (!pair.Value.IsAlive || !pair.Value.IsAttachedTo(roots))
            {
                pair.Value.Dispose();
                _treeObservations.Remove(pair.Key);
            }
    }

    private void DisposeTreeObservations()
    {
        foreach (var observation in _treeObservations.Values)
            observation.Dispose();
        _treeObservations.Clear();
    }

    // Event sources and the owner are weak: an expanded branch never owns the target tree or its session.
    private sealed class TreeBranchObservation : IDisposable
    {
        private readonly WeakReference<TreeInspectionService> _owner;
        private readonly WeakReference<object> _target;
        private readonly List<WeakReference<INotifyCollectionChanged>> _collections = [];
        private readonly WeakReference<TemplatedControl>? _templateOwner;
        private readonly WeakReference<ContentPresenter>? _presenter;
        private readonly long _id;
        private readonly TreeKind _kind;
        private string _childrenFingerprint;
        private bool _disposed;
        public string CursorIdentity { get; } = Guid.NewGuid().ToString("N");
        public long Revision { get; private set; }

        public TreeBranchObservation(TreeInspectionService owner, object target, long id, TreeKind kind)
        {
            _owner = new(owner);
            _target = new(target);
            _id = id;
            _kind = kind;
            _childrenFingerprint = owner.Fingerprint(owner.ChildObjects(target, kind));
            var source = target is MergedTree.SyntheticNode synthetic ? synthetic.Parent : target;
            if (kind is TreeKind.Visual or TreeKind.Merged && source is Visual visual &&
                visual.GetVisualChildren() is INotifyCollectionChanged visualChildren)
                Subscribe(visualChildren);
            if (kind is TreeKind.Logical or TreeKind.Merged && source is ILogical logical)
                Subscribe(logical.LogicalChildren);
            if (kind == TreeKind.Merged && source is TemplatedControl templated)
            {
                _templateOwner = new(templated);
                templated.TemplateApplied += TemplateApplied;
            }

            if (kind == TreeKind.Merged && source is ContentPresenter presenter)
            {
                _presenter = new(presenter);
                presenter.PropertyChanged += PresenterChanged;
            }
        }

        public bool IsAlive => _target.TryGetTarget(out _);

        public bool IsAttachedTo(IReadOnlyList<TopLevel> roots)
        {
            if (!_target.TryGetTarget(out var target))
                return false;
            if (target is MergedTree.SyntheticNode synthetic)
                target = synthetic.Parent;
            if (target is Visual visual && roots.Any(root => ReferenceEquals(root, TopLevel.GetTopLevel(visual))))
                return true;
            for (var depth = 0; target is ILogical logical && depth < 512; depth++, target = logical.LogicalParent)
                if (roots.Any(root => ReferenceEquals(root, target)))
                    return true;
            return false;
        }

        private void Subscribe(INotifyCollectionChanged collection)
        {
            if (_collections.Any(reference => reference.TryGetTarget(out var old) && ReferenceEquals(old, collection)))
                return;
            _collections.Add(new(collection));
            collection.CollectionChanged += Changed;
        }

        private void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (_disposed)
                return;
            if (!_owner.TryGetTarget(out var owner) || !_target.TryGetTarget(out var target))
            {
                Dispose();
                return;
            }

            try
            {
                // Compare the filtered branch only when its source raises an event. Reset and mixed
                // changes remain correct, while diagnostic overlay insertion/removal stays invisible.
                var fingerprint = owner.Fingerprint(owner.ChildObjects(target, _kind));
                if (fingerprint == _childrenFingerprint)
                    return;
                _childrenFingerprint = fingerprint;
                Publish();
            }
            catch (RpcException exception) when
                (exception.Code is "object_expired" or "object_detached" or "tree_limit")
            {
                Publish();
            }
        }

        private void TemplateApplied(object? sender, TemplateAppliedEventArgs args) => Publish();

        private void PresenterChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == ContentPresenter.ChildProperty)
                Publish();
        }

        private void Publish()
        {
            if (_disposed)
                return;
            Revision++;
            if (_owner.TryGetTarget(out var owner))
                owner.TreeBranchChanged(_id, _kind);
            else
                Dispose();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var reference in _collections)
                if (reference.TryGetTarget(out var collection))
                    collection.CollectionChanged -= Changed;
            _collections.Clear();
            if (_templateOwner?.TryGetTarget(out var templated) == true)
                templated.TemplateApplied -= TemplateApplied;
            if (_presenter?.TryGetTarget(out var presenter) == true)
                presenter.PropertyChanged -= PresenterChanged;
        }
    }

    private const int ControlTraversalLimit = 100000;

    private ControlTreeSnapshot GetOfficialTree(ControlTreeRequest request)
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
            ControlPath(parent, request.Kind, roots);
            start = ChildObjects(parent, request.Kind).ToArray();
            parentId = request.ObjectId;
        }

        if (request.Depth == 0)
            return new ControlTreeSnapshot([], 0);

        var result = new List<ControlTreeEntry>();
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
            result.Add(new ControlTreeEntry(node, entry.ParentId, entry.Depth));
            if (request.Depth != -1 && entry.Depth + 1 >= request.Depth)
                continue;
            var children = ChildObjects(entry.Value, request.Kind).ToArray();
            for (var index = children.Length - 1; index >= 0; index--)
                pending.Push((children[index], node.Id, entry.Depth + 1));
        }

        return new ControlTreeSnapshot(result, result.Count);
    }

    private IReadOnlyList<TreeNodeInfo> GetOfficialAncestors(long objectId)
    {
        var target = _objects.Get(objectId);
        return ControlPath(target, TreeKind.Merged, RefreshRoots()).Select(item => Describe(item, TreeKind.Merged))
            .ToArray();
    }

    private TreeSearchResult SearchOfficialTree(ControlSearchRequest request)
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
            ControlPath(scope, TreeKind.Merged, roots);
            Schedule(scope);
        }

        if (request.Limit == 0)
            return new TreeSearchResult([], 0, false);

        var matches = new List<TreeNodeInfo>();
        var visited = 0;
        while (pending.TryDequeue(out var current))
        {
            visited++;
            if (ControlMatches(current, request.Query))
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

    private static bool ControlMatches(object value, string query) => value is not MergedTree.SyntheticNode &&
                                                                      ((value.GetType().FullName ??
                                                                        value.GetType().Name).Contains(query,
                                                                           StringComparison.OrdinalIgnoreCase) ||
                                                                       value is StyledElement { Name: { } name } &&
                                                                       name.Contains(query,
                                                                           StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<object> ControlPath(object target, TreeKind kind, IReadOnlyList<TopLevel> roots)
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
        if (count > ControlTraversalLimit)
            throw new RpcException("tree_limit",
                "The operation exceeds the explicit 100000-node traversal safety limit. Narrow its scope.");
    }

    internal IReadOnlyList<TreeNodeInfo> GetAncestors(TreeAncestorsRequest request)
    {
        ValidateTreeKind(request.Kind);
        var target = _objects.Get(request.ObjectId);
        var roots = RefreshRoots();
        if (request.Kind == TreeKind.Merged)
            return MergedPath(target, roots).Select(item => Describe(item, request.Kind)).ToArray();
        var path = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object? current = target;
        while (current is not null && path.Count < 512)
        {
            if (!seen.Add(current))
                throw new RpcException("invalid_tree", "The ancestor chain contains a cycle.");
            path.Add(current);
            if (roots.Any(root => ReferenceEquals(root, current)))
            {
                path.Reverse();
                return path.Select(item => Describe(item, request.Kind)).ToArray();
            }

            current = request.Kind switch
            {
                TreeKind.Visual when current is Visual visual => visual.GetVisualParent(),
                TreeKind.Logical when current is ILogical logical => logical.LogicalParent,
                _ => null
            };
        }

        if (path.Count >= 512)
            throw new RpcException("tree_limit", "The ancestor chain exceeds the 512-node safety limit.");
        throw new RpcException("object_detached", "The object is not reachable from a current root in this tree.");
    }

    private TreeSearchResult SearchTree(TreeSearchRequest request)
    {
        ValidateTreeKind(request.Kind);
        if (request.MaxResults is < 1 or > 500 || request.MaxVisited is < 1 or > 20000)
            throw new RpcException("invalid_request", "MaxResults must be 1..500 and MaxVisited must be 1..20000.");
        if (request.Query is null || request.Query.Length > 1024)
            throw new RpcException("invalid_request", "The query must contain at most 1024 characters.");
        var query = request.Query.Trim();
        var pending = new Queue<object>();
        var scheduled = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var matches = new List<TreeNodeInfo>();
        var visited = 0;
        var truncated = false;

        void Schedule(object item)
        {
            if (scheduled.Contains(item))
                return;
            if (scheduled.Count >= request.MaxVisited)
            {
                truncated = true;
                return;
            }

            scheduled.Add(item);
            pending.Enqueue(item);
        }

        if (request.RootId is { } rootId)
        {
            var root = _objects.Get(rootId);
            if (request.Kind == TreeKind.Merged)
                MergedPath(root, RefreshRoots());
            Schedule(root);
        }
        else
            foreach (var root in RefreshRoots())
                Schedule(root);

        while (pending.TryDequeue(out var current))
        {
            visited++;
            if (Matches(current, query))
            {
                var node = Describe(current, request.Kind);
                matches.Add(node);
                if (matches.Count == request.MaxResults)
                {
                    truncated |= pending.Count > 0 || node.HasChildren;
                    break;
                }
            }

            foreach (var child in ChildObjects(current, request.Kind))
                Schedule(child);
        }

        return new TreeSearchResult(matches, visited, truncated);
    }

    private static bool Matches(object value, string query)
    {
        if (query.Length == 0)
            return true;
        if (value is MergedTree.SyntheticNode synthetic &&
            synthetic.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        if ((value.GetType().FullName ?? value.GetType().Name).Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;
        if (value is StyledElement styled && styled.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            return true;
        var preview = value switch
        {
            TextBlock text => text.Text,
            TextBox text => text.Text,
            ContentControl { Content: string text } => text,
            _ => null
        };
        return preview?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void ValidateTreeKind(TreeKind kind)
    {
        if (kind is not (TreeKind.Visual or TreeKind.Logical or TreeKind.Merged))
            throw new RpcException("invalid_request", "The requested tree kind is unsupported.");
    }

    private IReadOnlyList<object> MergedPath(object target, IReadOnlyList<TopLevel> roots)
    {
        var path = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object? current = target;
        while (current is not null && path.Count < 512)
        {
            if (!seen.Add(current))
                throw new RpcException("invalid_tree", "The merged ancestor chain contains a cycle.");
            if (current is MergedTree.SyntheticNode synthetic)
                synthetic.Validate();
            path.Add(current);
            if (roots.Any(root => ReferenceEquals(root, current)))
            {
                path.Reverse();
                return path;
            }

            current = _mergedTree.Parent(current);
        }

        if (path.Count >= 512)
            throw new RpcException("tree_limit", "The ancestor chain exceeds the 512-node safety limit.");
        throw new RpcException("object_detached",
            "The object is not reachable from a current root in the merged tree.");
    }
}
