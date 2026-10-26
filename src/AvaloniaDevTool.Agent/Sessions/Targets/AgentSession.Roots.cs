using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private readonly Dictionary<TopLevel, RootHandlers> _rootHandlers = [];

    private List<TopLevel> RefreshRoots()
    {
        var initial = _options.TopLevelProvider?.Invoke() ??
                      (_application.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
                      .Cast<TopLevel>() ??
                      [];
        var roots = initial.Distinct().ToList();
        _popupFreeze.RefreshRoots(roots);
        // OpenedPopups also includes the private Popup owned by a Flyout, ContextMenu or ToolTip.
        // Logical descendants omit those popups. Walk only popups owned by the configured roots.
        var pendingPopups = new Queue<Popup>(roots.SelectMany(root => root.OpenedPopups));
        var seenPopups = new HashSet<Popup>();
        while (pendingPopups.TryDequeue(out var popup))
        {
            if (!seenPopups.Add(popup) || !popup.IsOpen)
                continue;
            foreach (var nested in popup.OpenedPopups)
                pendingPopups.Enqueue(nested);
            if (popup.Child is Visual child && TopLevel.GetTopLevel(child) is { } popupRoot &&
                !roots.Contains(popupRoot))
                roots.Add(popupRoot);
        }

        foreach (var removed in _rootHandlers.Keys.Except(roots).ToArray())
        {
            _rootHandlers[removed].Dispose();
            _rootHandlers.Remove(removed);
        }

        foreach (var root in roots)
        {
            Observe(root);
            if (!_rootHandlers.ContainsKey(root))
            {
                _rootHandlers.Add(root,
                    new RootHandlers(root, OnPointerPressed, OnPointerMoved, OnKeyDown, OnGotFocus));
                // A newly opened native popup may have received focus before the discovery tick.
                if (_followFocus && root.FocusManager?.GetFocusedElement() is Control focused &&
                    ReferenceEquals(TopLevel.GetTopLevel(focused), root))
                    Notify(Methods.SelectionChanged, new SelectionChangedNotification(Observe(focused)));
            }
        }

        _rendererDiagnostics.RefreshRoots(roots);
        PruneTreeObservations(roots);
        return roots;
    }

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
