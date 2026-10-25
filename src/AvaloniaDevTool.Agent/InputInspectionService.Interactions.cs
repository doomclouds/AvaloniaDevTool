using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class InputInspectionService
{
    public InputResult Click(ClickInputRequest request) => WithTarget(request.ObjectId, target =>
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
            return new InputResult(true, handled, $"{request.ClickCount} routed {button} click(s) delivered; the control decides the business effect.");
        }
        finally { ReleasePointer(); }
    });

    public InputResult Chord(KeyChordRequest request) => WithTarget(request.ObjectId, target =>
    {
        if (request.Keys is null || request.Keys.Count is < 1 or > 16)
            throw new RpcException("input_limit", "A chord must contain between one and sixteen distinct named keys.");
        var modifiers = ParseModifiers(request.Modifiers);
        var keys = new List<Key>();
        foreach (var modifier in new[] { KeyModifiers.Control, KeyModifiers.Alt, KeyModifiers.Shift, KeyModifiers.Meta })
            if (modifiers.HasFlag(modifier)) keys.Add(ModifierKey(modifier));
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
                throw new RpcException("invalid_input", "Chord keys must be distinct and cannot contain None or duplicate a modifier.");
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
        catch (Exception exception) { failure = exception; }
        finally
        {
            for (var index = pressed.Count - 1; index >= 0; index--)
            {
                var key = pressed[index];
                pressed.RemoveAt(index);
                try { handled |= RaiseChordKey(target, key, false, HeldModifiers(pressed)).Handled; }
                catch (Exception exception) { failure ??= exception; }
            }
        }
        if (failure is not null) throw failure;
        return new InputResult(true, handled, "Chord delivered as ordered key presses and reverse-order releases, including modifier keys.");
    });

    public InputResult Drag(DragInputRequest request) => WithTarget(request.SourceObjectId, source =>
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
                if (waypoint is null) throw new RpcException("invalid_input", "Drag waypoints cannot be null.");
                var control = GetTarget(waypoint.ObjectId);
                var (waypointRoot, point) = Position(control, waypoint.X, waypoint.Y);
                if (!ReferenceEquals(root, waypointRoot))
                    throw new RpcException("cross_root_input", "All drag waypoints must share the source top-level.");
                points.Add((control, point));
            }
        }
        else
        {
            for (var index = 1; index <= request.Steps; index++)
                points.Add((target, new Point(start.X + (end.X - start.X) * index / request.Steps,
                    start.Y + (end.Y - start.Y) * index / request.Steps)));
        }
        if (points.Count == 0 || points[^1].Position != end) points.Add((target, end));
        var handled = false;
        try
        {
            handled |= RaiseMove(source, root, start, modifiers).Handled;
            _ = source.Focus(NavigationMethod.Pointer, modifiers);
            handled |= RaiseDown(source, root, start, modifiers, button).Handled;
            foreach (var point in points)
                handled |= RaiseMove(PointerRecipient(point.Target, root), root, point.Position, modifiers).Handled;
            handled |= RaiseUp(PointerRecipient(target, root), root, end, modifiers, button).Handled;
            return new InputResult(true, handled,
                $"Captured pointer drag delivered through {points.Count} moves in one top-level; no OS drag-and-drop data transfer was created.");
        }
        finally { ReleasePointer(); }
    });

    public TextSelectionResult SelectText(TextSelectionRequest request) => WithTarget(request.ObjectId, target =>
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
        textBox.SetCurrentValue(TextBox.SelectionEndProperty, request.Start + (request.Length ?? textLength - request.Start));
        if (request.ReplacementText is { } replacement)
        {
            if (replacement.Length == 0 && textBox.SelectionStart != textBox.SelectionEnd)
            {
                // Delete through the editing pipeline, retaining undo, bindings, and read-only behavior.
                RaiseChordKey(textBox, Key.Delete, true, KeyModifiers.None);
                RaiseChordKey(textBox, Key.Delete, false, KeyModifiers.None);
            }
            else if (replacement.Length != 0)
                SendText(textBox, new InputRequest(request.ObjectId, InputKind.Text, Text: replacement), KeyModifiers.None);
        }
        var selectedText = textBox.SelectedText;
        return new TextSelectionResult(textBox.SelectionStart, textBox.SelectionEnd, textBox.Text?.Length ?? 0,
            selectedText.Length > 2048 ? selectedText[..2048] : selectedText, selectedText.Length > 2048,
            request.ReplacementText is not null && !string.Equals(originalText, textBox.Text, StringComparison.Ordinal));
    });

    public ItemSelectionResult SelectItem(SelectItemRequest request) => WithTarget(request.ObjectId, target =>
    {
        if (target is not SelectingItemsControl selector)
            throw new RpcException("unsupported_object", "Index selection requires a SelectingItemsControl such as ComboBox, ListBox, or TabControl.");
        if (request.Index < -1 || request.Index >= selector.Items.Count)
            throw new RpcException("invalid_selection", "The item index must be -1 or inside the items collection.");
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
        return new ItemSelectionResult(selector.SelectedIndex == request.Index, selector.SelectedIndex,
            value is { Length: > 512 } ? value[..512] : value, selector.Items.Count);
    });

    public ControlActionResult PerformAction(ControlActionRequest request) => WithTarget(request.ObjectId, target =>
    {
        if (!Enum.IsDefined(request.Action))
            throw new RpcException("unsupported_input", "The control action is not supported.");
        var peer = ControlAutomationPeer.CreatePeerForElement(target);
        var invoke = peer?.GetProvider<IInvokeProvider>();
        var toggle = peer?.GetProvider<IToggleProvider>();
        var expand = peer?.GetProvider<IExpandCollapseProvider>();
        switch (request.Action)
        {
            case ControlAction.Invoke when invoke is not null: invoke.Invoke(); break;
            case ControlAction.Toggle when toggle is not null: toggle.Toggle(); break;
            case ControlAction.Expand when expand is not null: expand.Expand(); break;
            case ControlAction.Collapse when expand is not null: expand.Collapse(); break;
            default:
                throw new RpcException("unsupported_action", "The control's automation peer does not provide the requested action.");
        }
        return new ControlActionResult(true, request.Action, toggle?.ToggleState.ToString(),
            expand?.ExpandCollapseState.ToString(), "The public Avalonia automation provider performed the action; returned states were read after invocation.");
    });

    private T WithTarget<T>(long objectId, Func<Control, T> action)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        NormalizePointerState();
        var target = GetTarget(objectId);
        try { return action(target); }
        catch (RpcException) { throw; }
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
            throw new RpcException("invalid_coordinates", "Pointer coordinates must be finite and inside the target's local bounds.");
        var root = TopLevel.GetTopLevel(target)
            ?? throw new RpcException("detached_object", "The target has no attached top-level.");
        if (target.TranslatePoint(local, root) is not { } position || !double.IsFinite(position.X) || !double.IsFinite(position.Y))
            throw new RpcException("unsupported_transform", "The local pointer coordinate cannot be mapped to the top-level.");
        return (root, position);
    }

    private void EnsurePointerIdle()
    {
        if (_pressedButton is not null)
            throw new RpcException("input_state", "Finish the current pointer sequence before starting another gesture.");
    }

    private void ValidatePointerRoot(TopLevel root)
    {
        if (_pressedButton is not null && (_pressedRoot is null || !_pressedRoot.TryGetTarget(out var pressedRoot) || !ReferenceEquals(root, pressedRoot)))
            throw new RpcException("cross_root_input", "A pressed pointer sequence must remain within one top-level.");
    }

    private Control PointerRecipient(Control target, TopLevel root)
    {
        ValidatePointerRoot(root);
        var recipient = _pointer.Captured as Control ?? target;
        if (!recipient.IsAttachedToVisualTree() || !ReferenceEquals(TopLevel.GetTopLevel(recipient), root))
            throw new RpcException("input_state", "The pointer recipient detached or moved to another top-level during the gesture.");
        return recipient;
    }

    private static MouseButton ParseButton(string name)
    {
        var button = ParseName<MouseButton>(name, "mouse button");
        if (button == MouseButton.None) throw new RpcException("invalid_input", "MouseButton.None is not an input button.");
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
