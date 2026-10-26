using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Diagnostics;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Suppresses automatic light dismissal without cancelling application close requests.</summary>
internal sealed class PopupFreezeService : IDisposable
{
    private readonly WeakObjectRegistry _objects;
    private readonly Dictionary<long, WeakReference<TopLevel>> _roots = [];
    private readonly Dictionary<long, Entry> _entries = [];
    private readonly HashSet<long> _frozenRoots = [];
    private readonly List<string> _conflicts = [];
    private readonly IDisposable _openedSubscription;
    private bool _allRoots;
    private bool _disposed;

    public PopupFreezeService(WeakObjectRegistry objects)
    {
        _objects = objects;
        _openedSubscription = Popup.IsOpenProperty.Changed.Subscribe(new OpenObserver(this));
    }

    public void RefreshRoots(IReadOnlyList<TopLevel> roots)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        roots = roots.Where(root => root.PlatformImpl is not null).ToArray();
        var ids = roots.Select(_objects.GetId).ToHashSet();
        foreach (var id in _roots.Keys.Except(ids).ToArray())
        {
            foreach (var entry in _entries.Values.Where(entry => entry.RootId == id).ToArray())
                Release(entry);
            RemoveRoot(id);
        }

        foreach (var root in roots)
        {
            var id = _objects.GetId(root);
            if (!_roots.ContainsKey(id))
                root.Closed += OnRootClosed;
            _roots[id] = new(root);
        }

        RefreshPopups();
    }

    public PopupFreezeStatusDto Set(PopupFreezeSetRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request.RootId is { } id)
        {
            if (!_roots.TryGetValue(id, out var reference) || !reference.TryGetTarget(out _))
                throw new RpcException("root_not_found",
                    "The selected root is no longer in the configured root scope.");
            // Convert global mode into an explicit selection before changing one root.
            if (_allRoots)
            {
                _frozenRoots.UnionWith(_roots.Keys);
                _allRoots = false;
            }

            if (request.IsFrozen)
                _frozenRoots.Add(id);
            else
                _frozenRoots.Remove(id);
        }
        else
        {
            _allRoots = request.IsFrozen;
            _frozenRoots.Clear();
        }

        RefreshPopups();
        return Status();
    }

    public PopupFreezeStatusDto Status()
    {
        Dispatcher.UIThread.VerifyAccess();
        RefreshPopups();
        var roots = _roots.Select(pair => pair.Value.TryGetTarget(out var root)
            ? new PopupFreezeRootDto(pair.Key,
                root is Window window ? window.Title ?? "Window" : root.GetType().Name,
                IsFrozen(pair.Key))
            : null).OfType<PopupFreezeRootDto>().ToArray();
        var popups = _entries.Values.Select(entry => entry.Describe()).OfType<PopupFreezePopupDto>().ToArray();
        return new(_allRoots || _frozenRoots.Count > 0, _allRoots, roots, popups, _conflicts.ToArray());
    }

    public PopupFreezeStatusDto Resume()
    {
        Dispatcher.UIThread.VerifyAccess();
        _allRoots = false;
        _frozenRoots.Clear();
        foreach (var entry in _entries.Values.ToArray())
            Release(entry);
        return Status();
    }

    private bool IsFrozen(long id) => _allRoots || _frozenRoots.Contains(id);

    private void RefreshPopups()
    {
        if (_disposed)
            return;
        foreach (var entry in _entries.Values.ToArray())
        {
            if (!entry.Popup.TryGetTarget(out var popup) || !popup.IsOpen || !IsFrozen(entry.RootId))
                Release(entry);
            else
                entry.CheckOwnership();
        }

        foreach (var pair in _roots.ToArray())
        {
            if (!IsFrozen(pair.Key) || !pair.Value.TryGetTarget(out var root))
                continue;
            var pending = new Queue<Popup>(root.OpenedPopups);
            var visited = new HashSet<Popup>();
            while (pending.TryDequeue(out var popup))
            {
                if (!visited.Add(popup) || !popup.IsOpen)
                    continue;
                Observe(popup, pair.Key);
                foreach (var nested in popup.OpenedPopups)
                    pending.Enqueue(nested);
            }
        }
    }

    private void OnOpened(Popup popup)
    {
        if (_disposed || !popup.IsOpen || FindRoot(popup) is not { } rootId || !IsFrozen(rootId))
            return;
        Observe(popup, rootId);
    }

    private long? FindRoot(Popup popup)
    {
        var root = popup.PlacementTarget is { } target ? TopLevel.GetTopLevel(target) : null;
        var seen = new HashSet<TopLevel>();
        while (root is not null && seen.Add(root))
        {
            var id = _objects.GetId(root);
            if (_roots.TryGetValue(id, out var reference) && reference.TryGetTarget(out var configured) &&
                ReferenceEquals(configured, root))
                return id;
            root = root is PopupRoot { Parent: Popup parent } && parent.PlacementTarget is { } placement
                ? TopLevel.GetTopLevel(placement)
                : null;
        }

        return null;
    }

    private void Observe(Popup popup, long rootId)
    {
        var id = _objects.GetId(popup);
        if (_entries.ContainsKey(id))
            return;
        var entry = new Entry(this, popup, id, rootId);
        _entries.Add(id, entry);
        entry.Apply();
    }

    private void Release(Entry entry)
    {
        _entries.Remove(entry.Id);
        entry.RestoreAndDetach();
    }

    private void RemoveRoot(long id)
    {
        if (_roots.Remove(id, out var reference) && reference.TryGetTarget(out var root))
            root.Closed -= OnRootClosed;
        _frozenRoots.Remove(id);
    }

    private void OnRootClosed(object? sender, EventArgs args)
    {
        if (sender is not TopLevel root)
            return;
        var id = _objects.GetId(root);
        foreach (var entry in _entries.Values.Where(entry => entry.RootId == id).ToArray())
        {
            Release(entry);
            if (entry.Popup.TryGetTarget(out var popup) && popup.IsOpen)
                popup.Close();
        }

        RemoveRoot(id);
    }

    private void Conflict(Entry entry)
    {
        if (_conflicts.Count == 64)
            _conflicts.RemoveAt(0);
        _conflicts.Add($"Popup #{entry.Id}: application changed IsLightDismissEnabled; its later value is preserved.");
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        Resume();
        _openedSubscription.Dispose();
        foreach (var id in _roots.Keys.ToArray())
            RemoveRoot(id);
        _disposed = true;
    }

    private sealed class OpenObserver(PopupFreezeService owner) : IObserver<AvaloniaPropertyChangedEventArgs<bool>>
    {
        public void OnNext(AvaloniaPropertyChangedEventArgs<bool> value)
        {
            if (value.NewValue.GetValueOrDefault() && value.Sender is Popup popup)
                owner.OnOpened(popup);
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }

    private sealed class Entry
    {
        private readonly PopupFreezeService _owner;
        private bool _owned, _writing, _original;

        public Entry(PopupFreezeService owner, Popup popup, long id, long rootId)
        {
            _owner = owner;
            Popup = new(popup);
            Id = id;
            RootId = rootId;
            popup.PropertyChanged += OnPropertyChanged;
            popup.Closed += OnClosed;
        }

        public WeakReference<Popup> Popup { get; }
        public long Id { get; }
        public long RootId { get; }

        public void Apply()
        {
            if (!Popup.TryGetTarget(out var popup) || !popup.IsLightDismissEnabled)
                return;
            _original = popup.IsLightDismissEnabled;
            _owned = true;
            _writing = true;
            try
            {
                popup.SetCurrentValue(Avalonia.Controls.Primitives.Popup.IsLightDismissEnabledProperty, false);
            }
            finally
            {
                _writing = false;
            }

            CheckOwnership();
        }

        public void CheckOwnership()
        {
            if (!_owned || !Popup.TryGetTarget(out var popup))
                return;
            // SetValue(false) can replace our current-value override without an effective value change.
            if (!popup.IsLightDismissEnabled && popup
                    .GetDiagnostic(Avalonia.Controls.Primitives.Popup.IsLightDismissEnabledProperty)
                    .IsOverriddenCurrentValue)
                return;
            _owned = false;
            _owner.Conflict(this);
        }

        public PopupFreezePopupDto? Describe()
        {
            if (!Popup.TryGetTarget(out var popup))
                return null;
            var kind = popup.Child switch { ToolTip => "ToolTip", ContextMenu => "ContextMenu", _ => "Popup/Flyout" };
            var limitation = popup.Child switch
            {
                ToolTip =>
                    "ToolTipService pointer/timer dismissal is not suppressed; ToolTip.IsOpen and application close remain authoritative.",
                ContextMenu =>
                    "DefaultMenuInteractionHandler root pointer/focus dismissal, menu selection/Escape and application Close remain authoritative; overlay outside clicks can still close the menu.",
                _ => "Transient Flyout move-away, target detachment and application close remain authoritative."
            };
            return new(Id, RootId, kind, popup.IsUsingOverlayLayer, popup.IsLightDismissEnabled, _owned, limitation);
        }

        public void RestoreAndDetach()
        {
            CheckOwnership();
            if (!Popup.TryGetTarget(out var popup))
                return;
            popup.PropertyChanged -= OnPropertyChanged;
            popup.Closed -= OnClosed;
            if (!_owned)
                return;
            _owned = false;
            popup.SetCurrentValue(Avalonia.Controls.Primitives.Popup.IsLightDismissEnabledProperty, _original);
        }

        private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (!_writing && args.Property == Avalonia.Controls.Primitives.Popup.IsLightDismissEnabledProperty)
                CheckOwnership();
        }

        private void OnClosed(object? sender, EventArgs args) => _owner.Release(this);
    }
}
