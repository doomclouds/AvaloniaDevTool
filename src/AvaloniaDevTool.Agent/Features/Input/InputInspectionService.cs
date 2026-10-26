using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;
using System.Runtime.InteropServices;

namespace AvaloniaDevTool.Agent;

/// <summary>Delivers routed framework input with an independent, session-owned pointer.</summary>
internal sealed partial class InputInspectionService : IDisposable
{
    private readonly WeakObjectRegistry _objects;
    private readonly Pointer _pointer;
    private WeakReference<Control>? _pressedTarget;
    private WeakReference<TopLevel>? _pressedRoot;
    private MouseButton? _pressedButton;
    private bool _disposed;

    public InputInspectionService(WeakObjectRegistry objects)
    {
        Dispatcher.UIThread.VerifyAccess();
        _objects = objects;
        // Pointer's base PlatformCapture is a no-op; no MouseDevice or native device is used.
        _pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
    }

    public InputResult Send(InputRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(request.Kind))
            throw new RpcException("unsupported_input", "This input event kind is not supported.");
        NormalizePointerState();
        var target = _objects.Get(request.ObjectId) as Control
                     ?? throw new RpcException("unsupported_object", "Input targets must be application controls.");
        ValidateTarget(target);
        var modifiers = ParseModifiers(request.Modifiers);
        var button = ParseName<MouseButton>(request.Button, "mouse button");
        if (button == MouseButton.None)
            throw new RpcException("invalid_input", "MouseButton.None is not an input button.");

        try
        {
            return request.Kind switch
            {
                InputKind.Text => SendText(target, request, modifiers),
                InputKind.KeyDown or InputKind.KeyUp => SendKey(target, request, modifiers),
                InputKind.PointerMove or InputKind.PointerDown or InputKind.PointerUp or InputKind.Wheel
                    or InputKind.Click =>
                    SendPointer(target, request, modifiers, button),
                _ => throw new RpcException("unsupported_input", "This input event kind is not supported.")
            };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new RpcException("input_failed", $"The control input handler failed: {exception.Message}");
        }
    }

    private static InputResult SendText(Control target, InputRequest request, KeyModifiers modifiers)
    {
        if (string.IsNullOrEmpty(request.Text))
            throw new RpcException("invalid_input", "Text input must contain text.");
        if (request.Text.Length > 2048)
            throw new RpcException("input_limit", "Text input is limited to 2048 UTF-16 code units.");
        if (target is TextBox { IsReadOnly: true })
            throw new RpcException("readonly_target", "The selected text box is read-only.");
        FocusForKeyboard(target, modifiers);
        var args = new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Source = target,
            Text = request.Text
        };
        target.RaiseEvent(args);
        return Delivered(args, "TextInput");
    }

    private static InputResult SendKey(Control target, InputRequest request, KeyModifiers modifiers)
    {
        var key = ParseName<Key>(request.Key, "key");
        if (key == Key.None)
            throw new RpcException("invalid_input", "Key.None cannot be delivered.");
        FocusForKeyboard(target, modifiers);
        var args = new KeyEventArgs
        {
            RoutedEvent = request.Kind == InputKind.KeyDown ? InputElement.KeyDownEvent : InputElement.KeyUpEvent,
            Source = target,
            Key = key,
            KeyModifiers = modifiers,
            KeyDeviceType = KeyDeviceType.Keyboard,
            KeySymbol = request.Key?.Length == 1 ? request.Key : null
        };
        target.RaiseEvent(args);
        return Delivered(args, request.Kind.ToString());
    }

    private InputResult SendPointer(Control target, InputRequest request, KeyModifiers modifiers, MouseButton button)
    {
        var local = new Point(request.X ?? target.Bounds.Width / 2, request.Y ?? target.Bounds.Height / 2);
        if (!double.IsFinite(local.X) || !double.IsFinite(local.Y) ||
            local.X < 0 || local.Y < 0 || local.X > target.Bounds.Width || local.Y > target.Bounds.Height)
            throw new RpcException("invalid_coordinates",
                "Pointer coordinates must be finite and inside the target's local bounds.");
        var topLevel = TopLevel.GetTopLevel(target)
                       ?? throw new RpcException("detached_object", "The target has no attached top-level.");
        var point = target.TranslatePoint(local, topLevel);
        if (point is not { } position || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
            throw new RpcException("unsupported_transform",
                "The local pointer coordinate cannot be mapped to the top-level.");
        ValidatePointerRoot(topLevel);
        if (request.Kind is InputKind.PointerDown or InputKind.Click && _pressedButton is not null)
            throw new RpcException("input_state", "A pointer button is already pressed.");
        if (request.Kind == InputKind.PointerUp && _pressedButton != button)
            throw new RpcException("input_state", "PointerUp must match an earlier PointerDown button.");
        if (request.Kind == InputKind.Wheel && (!double.IsFinite(request.DeltaX) || !double.IsFinite(request.DeltaY)))
            throw new RpcException("invalid_coordinates", "Wheel deltas must be finite.");

        switch (request.Kind)
        {
            case InputKind.PointerMove:
                return Delivered(RaiseMove(PointerRecipient(target, topLevel), topLevel, position, modifiers),
                    "PointerMoved");
            case InputKind.PointerDown:
                return Delivered(RaiseDown(target, topLevel, position, modifiers, button), "PointerPressed");
            case InputKind.PointerUp:
                return Delivered(RaiseUp(PointerRecipient(target, topLevel), topLevel, position, modifiers, button),
                    "PointerReleased");
            case InputKind.Wheel:
                var wheel = new PointerWheelEventArgs(target,
                    _pointer,
                    topLevel,
                    position,
                    Timestamp(),
                    Properties(modifiers, PointerUpdateKind.Other),
                    modifiers,
                    new Vector(request.DeltaX, request.DeltaY));
                target.RaiseEvent(wheel);
                return Delivered(wheel, "PointerWheelChanged");
            case InputKind.Click:
                try
                {
                    var moved = RaiseMove(target, topLevel, position, modifiers);
                    _ = target.Focus(NavigationMethod.Pointer, modifiers);
                    var down = RaiseDown(target, topLevel, position, modifiers, button);
                    var up = RaiseUp(target, topLevel, position, modifiers, button);
                    return new InputResult(true,
                        moved.Handled || down.Handled || up.Handled,
                        "Click delivered as routed move/press/release; the control decides its business effect.");
                }
                finally
                {
                    ReleasePointer();
                }
            default:
                throw new RpcException("unsupported_input", "This pointer input event is not supported.");
        }
    }

    private PointerEventArgs RaiseMove(Control target, TopLevel topLevel, Point position, KeyModifiers modifiers)
    {
        var args = new PointerEventArgs(InputElement.PointerMovedEvent,
            target,
            _pointer,
            topLevel,
            position,
            Timestamp(),
            Properties(modifiers, PointerUpdateKind.Other),
            modifiers);
        target.RaiseEvent(args);
        return args;
    }

    private PointerPressedEventArgs RaiseDown(Control target,
        TopLevel topLevel,
        Point position,
        KeyModifiers modifiers,
        MouseButton button,
        int clickCount = 1)
    {
        _pressedButton = button;
        _pressedTarget = new WeakReference<Control>(target);
        _pressedRoot = new WeakReference<TopLevel>(topLevel);
        try
        {
            _pointer.Capture(target);
            var args = new PointerPressedEventArgs(target,
                _pointer,
                topLevel,
                position,
                Timestamp(),
                Properties(modifiers, UpdateKind(button, pressed: true)),
                modifiers,
                clickCount);
            target.RaiseEvent(args);
            return args;
        }
        catch
        {
            ReleasePointer();
            throw;
        }
    }

    private PointerReleasedEventArgs RaiseUp(Control target,
        TopLevel topLevel,
        Point position,
        KeyModifiers modifiers,
        MouseButton button)
    {
        try
        {
            var raw = (RawInputModifiers)modifiers;
            var args = new PointerReleasedEventArgs(target,
                _pointer,
                topLevel,
                position,
                Timestamp(),
                new PointerPointProperties(raw, UpdateKind(button, pressed: false)),
                modifiers,
                button);
            target.RaiseEvent(args);
            return args;
        }
        finally
        {
            ReleasePointer();
        }
    }

    private PointerPointProperties Properties(KeyModifiers modifiers, PointerUpdateKind updateKind)
    {
        var raw = (RawInputModifiers)modifiers;
        if (_pressedButton is { } pressed)
            raw |= ButtonFlag(pressed);
        return new PointerPointProperties(raw, updateKind);
    }

    private void NormalizePointerState()
    {
        if (_pressedButton is null)
            return;
        if (_pressedTarget is null || !_pressedTarget.TryGetTarget(out var target) ||
            !target.IsAttachedToVisualTree() || !target.IsEffectivelyEnabled || !target.IsEffectivelyVisible ||
            _pressedRoot is null || !_pressedRoot.TryGetTarget(out var root) ||
            !ReferenceEquals(TopLevel.GetTopLevel(target), root))
            ReleasePointer();
    }

    private void ReleasePointer()
    {
        _pressedButton = null;
        _pressedTarget = null;
        _pressedRoot = null;
        _pointer.Capture(null);
    }

    private static void ValidateTarget(Control target)
    {
        if (OverlayService.IsDiagnosticVisual(target))
            throw new RpcException("unsupported_object", "Diagnostic overlays are not input targets.");
        if (!target.IsAttachedToVisualTree() || TopLevel.GetTopLevel(target) is null)
            throw new RpcException("detached_object", "Attach the target to a visual tree before sending input.");
        if (!target.IsEffectivelyEnabled)
            throw new RpcException("disabled_target", "The control or an ancestor is disabled.");
        if (!target.IsEffectivelyVisible)
            throw new RpcException("hidden_target", "The control or an ancestor is hidden.");
        if (OperatingSystem.IsWindows() && TopLevel.GetTopLevel(target)?.TryGetPlatformHandle() is
                { HandleDescriptor: "HWND" } handle &&
            handle.Handle != 0 && !IsWindowEnabled(handle.Handle))
            throw new RpcException("disabled_window", "The owning native window is disabled by a modal dialog.");
        if (!double.IsFinite(target.Bounds.Width) || !double.IsFinite(target.Bounds.Height) ||
            target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
            throw new RpcException("invalid_layout", "The control must have finite, positive arranged bounds.");
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(nint window);

    private static void FocusForKeyboard(Control target, KeyModifiers modifiers)
    {
        if (!target.Focus(NavigationMethod.Unspecified, modifiers))
            throw new RpcException("focus_failed", "The selected control cannot receive keyboard focus.");
    }

    private static T ParseName<T>(string? name, string description) where T : struct, Enum
    {
        if (name is null || !Enum.GetNames<T>().Contains(name, StringComparer.OrdinalIgnoreCase) ||
            !Enum.TryParse<T>(name, ignoreCase: true, out var value))
            throw new RpcException("invalid_input", $"Use a named Avalonia {description} value.");
        return value;
    }

    private static KeyModifiers ParseModifiers(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return KeyModifiers.None;
        if (values.Count > 4)
            throw new RpcException("invalid_input", "At most four distinct modifiers are allowed.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = KeyModifiers.None;
        foreach (var name in values)
        {
            if (!seen.Add(name))
                throw new RpcException("invalid_input", "Modifiers cannot contain duplicate names.");
            var modifier = ParseName<KeyModifiers>(name, "modifier");
            if (modifier == KeyModifiers.None && values.Count != 1)
                throw new RpcException("invalid_input", "None cannot be combined with other modifiers.");
            result |= modifier;
        }

        return result;
    }

    private static RawInputModifiers ButtonFlag(MouseButton button) => button switch
    {
        MouseButton.Left => RawInputModifiers.LeftMouseButton,
        MouseButton.Right => RawInputModifiers.RightMouseButton,
        MouseButton.Middle => RawInputModifiers.MiddleMouseButton,
        MouseButton.XButton1 => RawInputModifiers.XButton1MouseButton,
        MouseButton.XButton2 => RawInputModifiers.XButton2MouseButton,
        _ => RawInputModifiers.None
    };

    private static PointerUpdateKind UpdateKind(MouseButton button, bool pressed) => (button, pressed) switch
    {
        (MouseButton.Left, true) => PointerUpdateKind.LeftButtonPressed,
        (MouseButton.Left, false) => PointerUpdateKind.LeftButtonReleased,
        (MouseButton.Right, true) => PointerUpdateKind.RightButtonPressed,
        (MouseButton.Right, false) => PointerUpdateKind.RightButtonReleased,
        (MouseButton.Middle, true) => PointerUpdateKind.MiddleButtonPressed,
        (MouseButton.Middle, false) => PointerUpdateKind.MiddleButtonReleased,
        (MouseButton.XButton1, true) => PointerUpdateKind.XButton1Pressed,
        (MouseButton.XButton1, false) => PointerUpdateKind.XButton1Released,
        (MouseButton.XButton2, true) => PointerUpdateKind.XButton2Pressed,
        (MouseButton.XButton2, false) => PointerUpdateKind.XButton2Released,
        _ => throw new RpcException("invalid_input", "Unsupported pointer button.")
    };

    private static ulong Timestamp() => unchecked((ulong)Environment.TickCount64);

    private static InputResult Delivered(RoutedEventArgs args, string name) =>
        new(true, args.Handled, $"{name} delivered to the selected control; handled reflects routed event state.");

    public void Dispose()
    {
        if (_disposed)
            return;
        Dispatcher.UIThread.VerifyAccess();
        _disposed = true;
        _pressedButton = null;
        _pressedTarget = null;
        _pressedRoot = null;
        _pointer.Dispose();
    }
}
