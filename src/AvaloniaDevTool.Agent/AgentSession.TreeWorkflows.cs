using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private readonly Dictionary<(long Id, TreeKind Kind), TreeBranchObservation> _treeObservations = [];

    private ElementSelectorResult GetElementSelector(long id) => _objects.Get(id) is StyledElement element
        ? ElementSelectorBuilder.Build(element, RefreshRoots())
        : new(false, null, false, 0, false, "Synthetic nodes have no Avalonia selector.");

    private ElementMetadata GetElementMetadata(long id)
    {
        var target = _objects.Get(id);
        var styled = target as StyledElement;
        return new(_info.SessionId, "native", id, target.GetType().FullName ?? target.GetType().Name,
            styled?.Name, styled?.Classes.ToArray() ?? [], GetElementSelector(id));
    }

    private TreeObservationResult ObserveTree(TreeObserveRequest request)
    {
        ValidateTreeKind(request.Kind);
        var target = _objects.Get(request.ObjectId);
        if (request.Kind == TreeKind.Merged) MergedPath(target, RefreshRoots());
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
            if (_treeObservations.Remove((id, request.Kind), out var observation)) observation.Dispose();
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
        foreach (var observation in _treeObservations.Values) observation.Dispose();
        _treeObservations.Clear();
    }

    // Event sources and the owner are weak: an expanded branch never owns the target tree or its session.
    private sealed class TreeBranchObservation : IDisposable
    {
        private readonly WeakReference<AgentSession> _owner;
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

        public TreeBranchObservation(AgentSession owner, object target, long id, TreeKind kind)
        {
            _owner = new(owner); _target = new(target); _id = id; _kind = kind;
            _childrenFingerprint = owner.Fingerprint(owner.ChildObjects(target, kind));
            var source = target is MergedTree.SyntheticNode synthetic ? synthetic.Parent : target;
            if (kind is TreeKind.Visual or TreeKind.Merged && source is Visual visual &&
                visual.GetVisualChildren() is INotifyCollectionChanged visualChildren) Subscribe(visualChildren);
            if (kind is TreeKind.Logical or TreeKind.Merged && source is ILogical logical) Subscribe(logical.LogicalChildren);
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
            if (!_target.TryGetTarget(out var target)) return false;
            if (target is MergedTree.SyntheticNode synthetic) target = synthetic.Parent;
            if (target is Visual visual && roots.Any(root => ReferenceEquals(root, TopLevel.GetTopLevel(visual)))) return true;
            for (var depth = 0; target is ILogical logical && depth < 512; depth++, target = logical.LogicalParent)
                if (roots.Any(root => ReferenceEquals(root, target))) return true;
            return false;
        }
        private void Subscribe(INotifyCollectionChanged collection)
        {
            if (_collections.Any(reference => reference.TryGetTarget(out var old) && ReferenceEquals(old, collection))) return;
            _collections.Add(new(collection));
            collection.CollectionChanged += Changed;
        }
        private void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (_disposed) return;
            if (!_owner.TryGetTarget(out var owner) || !_target.TryGetTarget(out var target)) { Dispose(); return; }
            try
            {
                // Compare the filtered branch only when its source raises an event. Reset and mixed
                // changes remain correct, while diagnostic overlay insertion/removal stays invisible.
                var fingerprint = owner.Fingerprint(owner.ChildObjects(target, _kind));
                if (fingerprint == _childrenFingerprint) return;
                _childrenFingerprint = fingerprint;
                Publish();
            }
            catch (RpcException exception) when (exception.Code is "object_expired" or "object_detached" or "tree_limit")
            { Publish(); }
        }
        private void TemplateApplied(object? sender, TemplateAppliedEventArgs args) => Publish();
        private void PresenterChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == ContentPresenter.ChildProperty) Publish();
        }
        private void Publish()
        {
            if (_disposed) return;
            Revision++;
            if (_owner.TryGetTarget(out var owner)) owner.TreeBranchChanged(_id, _kind);
            else Dispose();
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var reference in _collections)
                if (reference.TryGetTarget(out var collection)) collection.CollectionChanged -= Changed;
            _collections.Clear();
            if (_templateOwner?.TryGetTarget(out var templated) == true) templated.TemplateApplied -= TemplateApplied;
            if (_presenter?.TryGetTarget(out var presenter) == true) presenter.PropertyChanged -= PresenterChanged;
        }
    }
}
