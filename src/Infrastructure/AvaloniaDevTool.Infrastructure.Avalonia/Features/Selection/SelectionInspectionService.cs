using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaDevTool.Protocol;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace AvaloniaDevTool.Agent;

internal sealed class SelectionInspectionService : IDisposable
{
    private readonly WeakObjectRegistry _objects;
    private readonly OverlayService _overlay;
    private readonly Func<object, long> _observe;
    private readonly Func<List<TopLevel>> _refreshRoots;
    private readonly Action<string, object> _notify;
    private bool _disposed;

    internal SelectionInspectionService(WeakObjectRegistry objects,
        OverlayService overlay,
        Func<object, long> observe,
        Func<List<TopLevel>> refreshRoots,
        Action<string, object> notify)
    {
        _objects = objects;
        _overlay = overlay;
        _observe = observe;
        _refreshRoots = refreshRoots;
        _notify = notify;
    }

    private long Observe(object value) => _observe(value);
    private List<TopLevel> RefreshRoots() => _refreshRoots();
    private void Notify(string method, object payload) => _notify(method, payload);

    internal void RefreshRoots(IReadOnlyList<TopLevel> roots)
    {
        foreach (var removed in _rootHandlers.Keys.Except(roots).ToArray())
        {
            _rootHandlers[removed].Dispose();
            _rootHandlers.Remove(removed);
        }

        foreach (var root in roots)
        {
            if (_rootHandlers.ContainsKey(root))
                continue;
            _rootHandlers.Add(root, new RootHandlers(root, OnPointerPressed, OnPointerMoved, OnKeyDown, OnGotFocus));
            if (_followFocus && root.FocusManager?.GetFocusedElement() is Control focused &&
                ReferenceEquals(TopLevel.GetTopLevel(focused), root))
                Notify(Methods.SelectionChanged, new SelectionChangedNotification(Observe(focused)));
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _picking = false;
        ClearHighlight();
        foreach (var handlers in _rootHandlers.Values)
            handlers.Dispose();
        _rootHandlers.Clear();
    }

    private bool _picking;

    private bool _followFocus;

    internal void Register(RpcMethodRegistry routes)
    {
        routes.Map<HighlightRequest>(Methods.Highlight, request => SetHighlight(request.ObjectId));
        routes.Map<PickingRequest>(Methods.SetPicking, request => SetPicking(request.Enabled));
        routes.Map<FollowFocusRequest>(ExplorationMethods.FollowFocus, request => SetFollowFocus(request.Enabled));
    }

    private OperationResult SetHighlight(long? id)
    {
        ClearHighlight();
        if (id is not null)
        {
            if (_objects.Get(id.Value) is not Control control)
                throw new RpcException("unsupported_object", "Only controls can be highlighted.");
            _overlay.Show(control);
        }

        return new OperationResult();
    }

    private void ClearHighlight()
    {
        _overlay.Clear();
    }

    private OperationResult SetPicking(bool enabled)
    {
        _picking = enabled;
        if (!enabled)
            ClearHighlight();
        RefreshRoots();
        Notify(Methods.PickingChanged, new PickingChangedNotification(_picking));
        return new OperationResult();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_picking && e.Source is Control control && !OverlayService.IsDiagnosticVisual(control))
        {
            var current = _overlay.CurrentTarget;
            if (current != control)
            {
                ClearHighlight();
                _overlay.Show(control);
            }
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_picking || e.Source is not Control control || OverlayService.IsDiagnosticVisual(control))
            return;
        e.Handled = true;
        _picking = false;
        var id = Observe(control);
        SetHighlight(id);
        Notify(Methods.SelectionChanged, new SelectionChangedNotification(id));
        Notify(Methods.PickingChanged, new PickingChangedNotification(false));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_picking && e.Key == Key.Escape)
        {
            SetPicking(false);
            e.Handled = true;
        }
    }

    private OperationResult SetFollowFocus(bool enabled)
    {
        _followFocus = enabled;
        if (enabled)
        {
            foreach (var root in RefreshRoots())
                if (root.FocusManager?.GetFocusedElement() is Control focused)
                    Notify(Methods.SelectionChanged, new SelectionChangedNotification(Observe(focused)));
        }

        return new OperationResult();
    }

    private void OnGotFocus(object? sender, FocusChangedEventArgs args)
    {
        if (_followFocus && !_disposed && args.Source is Control control && !OverlayService.IsDiagnosticVisual(control))
            Notify(Methods.SelectionChanged, new SelectionChangedNotification(Observe(control)));
    }

    private readonly Dictionary<TopLevel, RootHandlers> _rootHandlers = [];

    private sealed class RootHandlers : IDisposable
    {
        private readonly TopLevel _root;
        private readonly EventHandler<PointerPressedEventArgs> _pressed;
        private readonly EventHandler<PointerEventArgs> _moved;
        private readonly EventHandler<KeyEventArgs> _key;
        private readonly EventHandler<FocusChangedEventArgs> _focus;

        public RootHandlers(TopLevel root,
            EventHandler<PointerPressedEventArgs> pressed,
            EventHandler<PointerEventArgs> moved,
            EventHandler<KeyEventArgs> key,
            EventHandler<FocusChangedEventArgs> focus)
        {
            _root = root;
            _pressed = pressed;
            _moved = moved;
            _key = key;
            _focus = focus;
            root.AddHandler(InputElement.PointerPressedEvent, pressed, RoutingStrategies.Tunnel, true);
            root.AddHandler(InputElement.PointerMovedEvent, moved, RoutingStrategies.Tunnel, true);
            root.AddHandler(InputElement.KeyDownEvent, key, RoutingStrategies.Tunnel, true);
            root.AddHandler(InputElement.GotFocusEvent, focus, RoutingStrategies.Bubble, true);
        }

        public void Dispose()
        {
            _root.RemoveHandler(InputElement.PointerPressedEvent, _pressed);
            _root.RemoveHandler(InputElement.PointerMovedEvent, _moved);
            _root.RemoveHandler(InputElement.KeyDownEvent, _key);
            _root.RemoveHandler(InputElement.GotFocusEvent, _focus);
        }
    }
}
