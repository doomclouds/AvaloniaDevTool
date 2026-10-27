using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;
using System.Runtime.InteropServices;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls.Primitives;

namespace AvaloniaDevTool.Agent;

internal sealed class InputInspectionService : IDisposable
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

    public InputResult Click(ClickInputRequest request) => WithTarget(request.ObjectId,
        target =>
        {
            if (request.ClickCount is < 1 or > 3)
                throw new RpcException("input_limit", "ClickCount must be between one and three.");
            EnsurePointerIdle();
            var modifiers = ParseModifiers(request.Modifiers);
            var button = ParseButton(request.Button);
            var (root, position) = Position(target, request.X, request.Y);
            var handled = false;
            try
            {
                handled |= RaiseMove(target, root, position, modifiers).Handled;
                _ = target.Focus(NavigationMethod.Pointer, modifiers);
                for (var count = 1; count <= request.ClickCount; count++)
                {
                    ValidateTarget(target);
                    (root, position) = Position(target, request.X, request.Y);
                    handled |= RaiseDown(target, root, position, modifiers, button, count).Handled;
                    handled |= RaiseUp(PointerRecipient(target, root), root, position, modifiers, button).Handled;
                }

                return new InputResult(true,
                    handled,
                    $"{request.ClickCount} routed {button} click(s) delivered; the control decides the business effect.");
            }
            finally
            {
                ReleasePointer();
            }
        });

    public InputResult Chord(KeyChordRequest request) => WithTarget(request.ObjectId,
        target =>
        {
            if (request.Keys is null || request.Keys.Count is < 1 or > 16)
                throw new RpcException("input_limit",
                    "A chord must contain between one and sixteen distinct named keys.");
            var modifiers = ParseModifiers(request.Modifiers);
            var keys = new List<Key>();
            foreach (var modifier in new[]
                         { KeyModifiers.Control, KeyModifiers.Alt, KeyModifiers.Shift, KeyModifiers.Meta })
                if (modifiers.HasFlag(modifier))
                    keys.Add(ModifierKey(modifier));
            foreach (var name in request.Keys)
            {
                var key = name?.ToUpperInvariant() switch
                {
                    "CONTROL" => Key.LeftCtrl,
                    "SHIFT" => Key.LeftShift,
                    "ALT" => Key.LeftAlt,
                    "META" => Key.LWin,
                    _ => ParseName<Key>(name, "key")
                };
                if (key == Key.None || keys.Contains(key))
                    throw new RpcException("invalid_input",
                        "Chord keys must be distinct and cannot contain None or duplicate a modifier.");
                keys.Add(key);
            }

            // Build and validate the entire sequence before focusing or raising any key event.
            FocusForKeyboard(target, KeyModifiers.None);
            var pressed = new List<Key>();
            var handled = false;
            Exception? failure = null;
            try
            {
                foreach (var key in keys)
                {
                    pressed.Add(key);
                    handled |= RaiseChordKey(target, key, true, HeldModifiers(pressed)).Handled;
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                for (var index = pressed.Count - 1; index >= 0; index--)
                {
                    var key = pressed[index];
                    pressed.RemoveAt(index);
                    try
                    {
                        handled |= RaiseChordKey(target, key, false, HeldModifiers(pressed)).Handled;
                    }
                    catch (Exception exception)
                    {
                        failure ??= exception;
                    }
                }
            }

            if (failure is not null)
                throw failure;
            return new InputResult(true,
                handled,
                "Chord delivered as ordered key presses and reverse-order releases, including modifier keys.");
        });

    public InputResult Drag(DragInputRequest request) => WithTarget(request.SourceObjectId,
        source =>
        {
            EnsurePointerIdle();
            if (request.Steps is < 1 or > 128 || request.Path?.Count > 128)
                throw new RpcException("input_limit", "Drag steps and explicit waypoints are limited to 128.");
            var modifiers = ParseModifiers(request.Modifiers);
            var button = ParseButton(request.Button);
            var (root, start) = Position(source, request.SourceX, request.SourceY);
            var target = GetTarget(request.TargetObjectId);
            var (targetRoot, end) = Position(target, request.TargetX, request.TargetY);
            if (!ReferenceEquals(root, targetRoot))
                throw new RpcException("cross_root_input", "A framework pointer drag must stay within one top-level.");
            var points = new List<(Control Target, Point Position)>();
            if (request.Path is { Count: > 0 })
            {
                foreach (var waypoint in request.Path)
                {
                    if (waypoint is null)
                        throw new RpcException("invalid_input", "Drag waypoints cannot be null.");
                    var control = GetTarget(waypoint.ObjectId);
                    var (waypointRoot, point) = Position(control, waypoint.X, waypoint.Y);
                    if (!ReferenceEquals(root, waypointRoot))
                        throw new RpcException("cross_root_input",
                            "All drag waypoints must share the source top-level.");
                    points.Add((control, point));
                }
            }
            else
            {
                for (var index = 1; index <= request.Steps; index++)
                    points.Add((target, new Point(start.X + (end.X - start.X) * index / request.Steps,
                        start.Y + (end.Y - start.Y) * index / request.Steps)));
            }

            if (points.Count == 0 || points[^1].Position != end)
                points.Add((target, end));
            var handled = false;
            try
            {
                handled |= RaiseMove(source, root, start, modifiers).Handled;
                _ = source.Focus(NavigationMethod.Pointer, modifiers);
                handled |= RaiseDown(source, root, start, modifiers, button).Handled;
                foreach (var point in points)
                    handled |= RaiseMove(PointerRecipient(point.Target, root), root, point.Position, modifiers).Handled;
                handled |= RaiseUp(PointerRecipient(target, root), root, end, modifiers, button).Handled;
                return new InputResult(true,
                    handled,
                    $"Captured pointer drag delivered through {points.Count} moves in one top-level; no OS drag-and-drop data transfer was created.");
            }
            finally
            {
                ReleasePointer();
            }
        });

    public TextSelectionResult SelectText(TextSelectionRequest request) => WithTarget(request.ObjectId,
        target =>
        {
            if (target is not TextBox textBox)
                throw new RpcException("unsupported_object", "Text selection requires a TextBox.");
            var originalText = textBox.Text;
            var textLength = originalText?.Length ?? 0;
            if (request.Start < 0 || request.Start > textLength || request.Length is < 0 ||
                request.Length is { } length && length > textLength - request.Start)
                throw new RpcException("invalid_selection", "The UTF-16 selection range must lie within the text.");
            if (request.ReplacementText?.Length > 2048)
                throw new RpcException("input_limit", "Replacement text is limited to 2048 UTF-16 code units.");
            if (request.ReplacementText is not null && textBox.IsReadOnly)
                throw new RpcException("readonly_target", "The selected text box is read-only.");
            FocusForKeyboard(textBox, KeyModifiers.None);
            textBox.SetCurrentValue(TextBox.SelectionStartProperty, request.Start);
            textBox.SetCurrentValue(TextBox.SelectionEndProperty,
                request.Start + (request.Length ?? textLength - request.Start));
            if (request.ReplacementText is { } replacement)
            {
                if (replacement.Length == 0 && textBox.SelectionStart != textBox.SelectionEnd)
                {
                    // Delete through the editing pipeline, retaining undo, bindings, and read-only behavior.
                    RaiseChordKey(textBox, Key.Delete, true, KeyModifiers.None);
                    RaiseChordKey(textBox, Key.Delete, false, KeyModifiers.None);
                }
                else if (replacement.Length != 0)
                    SendText(textBox,
                        new InputRequest(request.ObjectId, InputKind.Text, Text: replacement),
                        KeyModifiers.None);
            }

            var selectedText = textBox.SelectedText;
            return new TextSelectionResult(textBox.SelectionStart,
                textBox.SelectionEnd,
                textBox.Text?.Length ?? 0,
                selectedText.Length > 2048 ? selectedText[..2048] : selectedText,
                selectedText.Length > 2048,
                request.ReplacementText is not null &&
                !string.Equals(originalText, textBox.Text, StringComparison.Ordinal));
        });

    public ItemSelectionResult SelectItem(SelectItemRequest request) => WithTarget(request.ObjectId,
        target =>
        {
            if (target is not SelectingItemsControl selector)
                throw new RpcException("unsupported_object",
                    "Index selection requires a SelectingItemsControl such as ComboBox, ListBox, or TabControl.");
            if (request.Index < -1 || request.Index >= selector.Items.Count)
                throw new RpcException("invalid_selection",
                    "The item index must be -1 or inside the items collection.");
            if (request.Index >= 0 && (selector.ContainerFromIndex(request.Index) is { IsEffectivelyEnabled: false } ||
                                       selector.Items[request.Index] is Control { IsEnabled: false }))
                throw new RpcException("disabled_target", "The selected item is disabled.");
            selector.SetCurrentValue(SelectingItemsControl.SelectedIndexProperty, request.Index);
            var value = selector.SelectedItem switch
            {
                HeaderedContentControl headered => headered.Header?.ToString(),
                ContentControl content => content.Content?.ToString(),
                null => null,
                var item => item.ToString()
            };
            return new ItemSelectionResult(selector.SelectedIndex == request.Index,
                selector.SelectedIndex,
                value is { Length: > 512 } ? value[..512] : value,
                selector.Items.Count);
        });

    public ControlActionResult PerformAction(ControlActionRequest request) => WithTarget(request.ObjectId,
        target =>
        {
            if (!Enum.IsDefined(request.Action))
                throw new RpcException("unsupported_input", "The control action is not supported.");
            var peer = ControlAutomationPeer.CreatePeerForElement(target);
            var invoke = peer?.GetProvider<IInvokeProvider>();
            var toggle = peer?.GetProvider<IToggleProvider>();
            var expand = peer?.GetProvider<IExpandCollapseProvider>();
            switch (request.Action)
            {
                case ControlAction.Invoke when invoke is not null:
                    invoke.Invoke();
                    break;
                case ControlAction.Toggle when toggle is not null:
                    toggle.Toggle();
                    break;
                case ControlAction.Expand when expand is not null:
                    expand.Expand();
                    break;
                case ControlAction.Collapse when expand is not null:
                    expand.Collapse();
                    break;
                default:
                    throw new RpcException("unsupported_action",
                        "The control's automation peer does not provide the requested action.");
            }

            return new ControlActionResult(true,
                request.Action,
                toggle?.ToggleState.ToString(),
                expand?.ExpandCollapseState.ToString(),
                "The public Avalonia automation provider performed the action; returned states were read after invocation.");
        });

    private T WithTarget<T>(long objectId, Func<Control, T> action)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        NormalizePointerState();
        var target = GetTarget(objectId);
        try
        {
            return action(target);
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

    private Control GetTarget(long objectId)
    {
        var target = _objects.Get(objectId) as Control
                     ?? throw new RpcException("unsupported_object", "Input targets must be application controls.");
        ValidateTarget(target);
        return target;
    }

    private static (TopLevel Root, Point Position) Position(Control target, double? x, double? y)
    {
        var local = new Point(x ?? target.Bounds.Width / 2, y ?? target.Bounds.Height / 2);
        if (!double.IsFinite(local.X) || !double.IsFinite(local.Y) || local.X < 0 || local.Y < 0 ||
            local.X > target.Bounds.Width || local.Y > target.Bounds.Height)
            throw new RpcException("invalid_coordinates",
                "Pointer coordinates must be finite and inside the target's local bounds.");
        var root = TopLevel.GetTopLevel(target)
                   ?? throw new RpcException("detached_object", "The target has no attached top-level.");
        if (target.TranslatePoint(local, root) is not { } position || !double.IsFinite(position.X) ||
            !double.IsFinite(position.Y))
            throw new RpcException("unsupported_transform",
                "The local pointer coordinate cannot be mapped to the top-level.");
        return (root, position);
    }

    private void EnsurePointerIdle()
    {
        if (_pressedButton is not null)
            throw new RpcException("input_state",
                "Finish the current pointer sequence before starting another gesture.");
    }

    private void ValidatePointerRoot(TopLevel root)
    {
        if (_pressedButton is not null && (_pressedRoot is null || !_pressedRoot.TryGetTarget(out var pressedRoot) ||
                                           !ReferenceEquals(root, pressedRoot)))
            throw new RpcException("cross_root_input", "A pressed pointer sequence must remain within one top-level.");
    }

    private Control PointerRecipient(Control target, TopLevel root)
    {
        ValidatePointerRoot(root);
        var recipient = _pointer.Captured as Control ?? target;
        if (!recipient.IsAttachedToVisualTree() || !ReferenceEquals(TopLevel.GetTopLevel(recipient), root))
            throw new RpcException("input_state",
                "The pointer recipient detached or moved to another top-level during the gesture.");
        return recipient;
    }

    private static MouseButton ParseButton(string name)
    {
        var button = ParseName<MouseButton>(name, "mouse button");
        if (button == MouseButton.None)
            throw new RpcException("invalid_input", "MouseButton.None is not an input button.");
        return button;
    }

    private static KeyEventArgs RaiseChordKey(Control target, Key key, bool pressed, KeyModifiers modifiers)
    {
        var args = new KeyEventArgs
        {
            RoutedEvent = pressed ? InputElement.KeyDownEvent : InputElement.KeyUpEvent,
            Source = target,
            Key = key,
            KeyModifiers = modifiers,
            KeyDeviceType = KeyDeviceType.Keyboard
        };
        target.RaiseEvent(args);
        return args;
    }

    private static Key ModifierKey(KeyModifiers modifier) => modifier switch
    {
        KeyModifiers.Control => Key.LeftCtrl,
        KeyModifiers.Alt => Key.LeftAlt,
        KeyModifiers.Shift => Key.LeftShift,
        KeyModifiers.Meta => Key.LWin,
        _ => throw new RpcException("invalid_input", "Unsupported chord modifier.")
    };

    private static KeyModifiers HeldModifiers(IEnumerable<Key> pressed)
    {
        var result = KeyModifiers.None;
        foreach (var key in pressed)
            result |= key switch
            {
                Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
                Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
                Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
                Key.LWin or Key.RWin => KeyModifiers.Meta,
                _ => KeyModifiers.None
            };
        return result;
    }
}
