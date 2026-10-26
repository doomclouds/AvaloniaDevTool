using System.Diagnostics;
using System.Runtime.InteropServices;
using AvaloniaDevTool.NativeAutomation;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal sealed partial class NativeAutomationBackend
{
    private NativeInputResult Input(NativeInputRequest request)
    {
        using var coordinates = NativeCoordinateSpace.Enter();
        return InputCore(request);
    }

    private NativeInputResult InputCore(NativeInputRequest request)
    {
        var entry = Resolve(request.NodeId);
        if (!entry.Element.Properties.IsEnabled.ValueOrDefault || entry.Element.Properties.IsOffscreen.ValueOrDefault)
            throw new NativeAutomationException("element_not_interactive",
                "The native input target is disabled or offscreen.");
        var kind = request.Kind.ToLowerInvariant();
        if (kind is not ("click" or "doubleclick" or "rightclick" or "move" or "wheel" or "text" or "keychord"
            or "drag"))
            throw new NativeAutomationException("unsupported_input", $"Unknown native input kind '{request.Kind}'.");
        var inputs = new List<OsInput>();
        var releases = new List<OsInput>();
        var points = new List<(NodeEntry Entry, ScreenPoint Point)>();
        var modifiers = ParseKeys(request.Modifiers);
        if (modifiers.Any(key => key is not (0x10 or 0x11 or 0x12)))
            throw new NativeAutomationException("invalid_argument", "Modifiers must be Ctrl, Alt, or Shift.");
        foreach (var modifier in modifiers)
        {
            inputs.Add(Key(modifier, false));
            releases.Insert(0, Key(modifier, true));
        }

        if (kind == "text")
        {
            if (request.Text is null || request.Text.Length is < 1 or > 4096 || modifiers.Length > 0)
                throw new NativeAutomationException("invalid_argument",
                    "Text requires 1..4096 UTF-16 characters and no modifiers.");
            foreach (var character in request.Text)
            {
                inputs.Add(Unicode(character, false));
                inputs.Add(Unicode(character, true));
                releases.Add(Unicode(character, true));
            }
        }
        else if (kind == "keychord")
        {
            var keys = ParseKeys(request.Keys);
            if (keys.Length is < 1 or > 8)
                throw new NativeAutomationException("invalid_argument", "KeyChord requires 1..8 keys.");
            var allKeys = modifiers.Concat(keys).ToHashSet();
            if ((allKeys.Contains(0x12) && (allKeys.Contains(0x09) || allKeys.Contains(0x1B))) ||
                (allKeys.Contains(0x11) && allKeys.Contains(0x1B)) ||
                (allKeys.Contains(0x11) && allKeys.Contains(0x12) && allKeys.Contains(0x2E)))
                throw new NativeAutomationException("out_of_scope",
                    "This system shortcut would leave the attached application's input scope.");
            foreach (var key in keys)
            {
                inputs.Add(Key(key, false));
                releases.Insert(0, Key(key, true));
            }
        }
        else
        {
            var start = Point(entry, request.X, request.Y);
            points.Add((entry, start));
            inputs.Add(Move(start));
            switch (kind)
            {
                case "click":
                    inputs.Add(Mouse(0x0002));
                    inputs.Add(Mouse(0x0004));
                    releases.Insert(0, Mouse(0x0004));
                    break;
                case "doubleclick":
                    inputs.Add(Mouse(0x0002));
                    inputs.Add(Mouse(0x0004));
                    inputs.Add(Mouse(0x0002));
                    inputs.Add(Mouse(0x0004));
                    releases.Insert(0, Mouse(0x0004));
                    break;
                case "rightclick":
                    inputs.Add(Mouse(0x0008));
                    inputs.Add(Mouse(0x0010));
                    releases.Insert(0, Mouse(0x0010));
                    break;
                case "wheel":
                    if (request.WheelDelta is 0 or < -12000 or > 12000)
                        throw new NativeAutomationException("invalid_argument",
                            "WheelDelta must be nonzero and between -12000 and 12000.");
                    inputs.Add(Mouse(0x0800, unchecked((uint)request.WheelDelta)));
                    break;
                case "drag":
                    if (request.TargetNodeId is null || request.DragSteps is < 1 or > 128)
                        throw new NativeAutomationException("invalid_argument",
                            "Drag requires TargetNodeId and 1..128 DragSteps.");
                    var target = Resolve(request.TargetNodeId);
                    if (target.Root.Handle != entry.Root.Handle)
                        throw new NativeAutomationException("out_of_scope",
                            "A native drag must start and end in the same scoped window.");
                    var end = Point(target, request.TargetX, request.TargetY);
                    points.Add((target, end));
                    inputs.Add(Mouse(0x0002));
                    for (var step = 1; step <= request.DragSteps; step++)
                    {
                        var intermediate = new ScreenPoint(start.X + (end.X - start.X) * step / request.DragSteps,
                            start.Y + (end.Y - start.Y) * step / request.DragSteps);
                        points.Add((entry, intermediate));
                        inputs.Add(Move(intermediate));
                    }

                    inputs.Add(Mouse(0x0004));
                    releases.Insert(0, Mouse(0x0004));
                    break;
            }
        }

        // Every held key/button is released within the same OS insertion batch.
        // There is no delay or await between a down and its matching up event.
        foreach (var release in releases.Where(input => input.Type == 1 && (input.Data.Keyboard.Flags & 0x0004) == 0))
            inputs.Add(release);
        ValidateEntry(entry);
        EnsureNoPhysicalInput();
        var keyboardInput = kind is "text" or "keychord";
        var activation = ActivateForInput(entry, keyboardInput);
        if (keyboardInput)
        {
            if (!entry.Element.Properties.HasKeyboardFocus.ValueOrDefault)
                throw new NativeAutomationException("focus_denied",
                    "Windows did not focus the scoped native input target.");
        }

        EnsureForeground(entry, activation, "after-focus");
        foreach (var point in points)
            ValidatePoint(point.Entry, point.Point);
        var batch = inputs.ToArray();
        EnsureNoPhysicalInput();
        EnsureForeground(entry, activation);
        var sent = 0u;
        string? moveConfirmation = null;
        if (points.Count > 0)
        {
            var moveIndex = Array.FindIndex(batch, input => input.Type == 0 && (input.Data.Mouse.Flags & 1) != 0);
            if (moveIndex >= 0)
            {
                sent = InsertInputs([batch[moveIndex]]);
                var expectedPoint = points[0].Point;
                var reached = NativeCoordinateSpace.ReadCursor(out var actualX, out var actualY);
                var actualPoint = new ScreenPoint(actualX, actualY);
                moveConfirmation =
                    $"requested=({expectedPoint.X},{expectedPoint.Y}),actual=({actualPoint.X},{actualPoint.Y}),inserted={sent}";
                if (sent != 1 || !reached || Math.Abs(actualPoint.X - expectedPoint.X) > 1 ||
                    Math.Abs(actualPoint.Y - expectedPoint.Y) > 1)
                    throw new NativeAutomationException("input_not_arrived",
                        $"The cursor move did not reach its target; no buttons or keys were injected. {moveConfirmation}");
                EnsureNoPhysicalInput();
                EnsureForeground(entry, activation, "after-move-confirmation");
                foreach (var point in points)
                    ValidatePoint(point.Entry, point.Point, pointerMoved: true);
                batch = batch.Where((_, index) => index != moveIndex).ToArray();
            }
        }

        ValidateLifetimes(entry);
        sent += InsertInputBatch(batch, InsertInputs, sent);
        return new NativeInputResult(request.NodeId,
            request.Kind,
            true,
            checked((int)sent),
            Note:
            $"Windows inserted {sent} input events. Verify application state separately; Unicode injection is not an IME composition or an OS file drag-and-drop assertion." +
            (moveConfirmation is null ? "" : $" MoveConfirmation={moveConfirmation}"));
    }

    private bool ActivateForInput(NodeEntry entry, bool keyboardInput)
    {
        if (!NativeWindows.IsWindowEnabled(entry.Root.Handle))
            throw new NativeAutomationException("element_not_interactive",
                "Windows has disabled the scoped native input window.");
        var previous = GetForegroundWindow();
        var previousRoot = NativeWindows.GetAncestor(previous, 2);
        if (IsIconic(entry.Root.Handle))
            ShowWindowAsync(entry.Root.Handle, 9);
        var activationWatch = Stopwatch.StartNew();
        var activation = SetForegroundWindow(entry.Root.Handle);
        var actual = GetForegroundWindow();
        var actualRoot = actual == 0 ? 0 : NativeWindows.GetAncestor(actual, 2);
        if (actualRoot != entry.Root.Handle && actualRoot != previousRoot)
        {
            if (keyboardInput)
                throw KeyboardActivationFailure(entry,
                    previousRoot,
                    activation,
                    activationWatch,
                    null,
                    "activation-interrupted");
            EnsureForeground(entry, activation, "activation-interrupted");
        }

        if (keyboardInput)
        {
            // Focus the requested keyboard element before confirming its root. An
            // owner activation can otherwise be redirected to a recent owned window.
            entry.Element.FrameworkAutomationElement.SetFocus();
        }
        else if (!activation && actualRoot != entry.Root.Handle)
        {
            // UIA SetFocus is a supported accessibility activation path. It does not
            // attach input queues, alter foreground policy, or bypass scope checks.
            _automation.FromHandle(entry.Root.Handle).FrameworkAutomationElement.SetFocus();
        }

        // SetForegroundWindow nudges another input queue asynchronously. WM_NULL
        // provides a bounded barrier so the target processes that activation first.
        var confirmationWatch = Stopwatch.StartNew();
        var responsive = SendMessageTimeout(entry.Root.Handle, 0, 0, 0, 0x22, 750, out _) != 0;
        if (keyboardInput)
        {
            if (!responsive)
                throw KeyboardActivationFailure(entry,
                    previousRoot,
                    activation,
                    activationWatch,
                    null,
                    "activation-barrier-timeout");
            WaitForKeyboardActivation(entry, previousRoot, activation, activationWatch, confirmationWatch);
            return activation;
        }

        EnsureNoPhysicalInput();
        EnsureForeground(entry, activation, "after-activation-barrier");
        if (!responsive)
            throw new NativeAutomationException("foreground_changed",
                "The scoped window did not process its activation within 750 ms; no input was injected.");
        return activation;
    }

    private void WaitForKeyboardActivation(NodeEntry entry,
        nint previousRoot,
        bool activation,
        Stopwatch activationWatch,
        Stopwatch confirmationWatch)
    {
        bool? hasKeyboardFocus = null;
        using var statePoll = new ManualResetEventSlim(false);
        while (true)
        {
            if (confirmationWatch.ElapsedMilliseconds >= 750)
                throw KeyboardActivationFailure(entry,
                    previousRoot,
                    activation,
                    activationWatch,
                    hasKeyboardFocus,
                    "activation-state-timeout");
            ValidateTarget();
            EnsureNoPhysicalInput();
            if (!IsScopedWindow(entry.Root.Handle) ||
                NativeWindows.ProcessId(entry.Root.Handle) != entry.Root.ProcessId ||
                ProcessStartTicks(entry.Root.ProcessId) != entry.Root.StartTicks)
                throw new NativeAutomationException("out_of_scope",
                    "The native input window changed ownership during activation.");
            if (!NativeWindows.IsWindowEnabled(entry.Root.Handle))
                throw new NativeAutomationException("element_not_interactive",
                    "Windows disabled the scoped native input window during activation.");
            var actual = GetForegroundWindow();
            var actualRoot = actual == 0 ? 0 : NativeWindows.GetAncestor(actual, 2);
            if (actualRoot != entry.Root.Handle && actualRoot != previousRoot)
                throw KeyboardActivationFailure(entry,
                    previousRoot,
                    activation,
                    activationWatch,
                    hasKeyboardFocus,
                    "activation-interrupted");
            if (actualRoot == entry.Root.Handle && NativeWindows.ProcessId(actual) == entry.Root.ProcessId)
            {
                hasKeyboardFocus = entry.Element.Properties.HasKeyboardFocus.ValueOrDefault;
                // Observe state only. The activation and SetFocus requests are never
                // repeated while Windows finishes processing the original requests.
                var confirmed = GetForegroundWindow();
                var confirmedRoot = confirmed == 0 ? 0 : NativeWindows.GetAncestor(confirmed, 2);
                if (confirmedRoot != entry.Root.Handle && confirmedRoot != previousRoot)
                    throw KeyboardActivationFailure(entry,
                        previousRoot,
                        activation,
                        activationWatch,
                        hasKeyboardFocus,
                        "activation-interrupted");
                if (hasKeyboardFocus == true && confirmedRoot == entry.Root.Handle &&
                    NativeWindows.ProcessId(confirmed) == entry.Root.ProcessId &&
                    confirmationWatch.ElapsedMilliseconds <= 750)
                    return;
            }

            var remaining = 750 - confirmationWatch.ElapsedMilliseconds;
            if (remaining <= 0)
                throw KeyboardActivationFailure(entry,
                    previousRoot,
                    activation,
                    activationWatch,
                    hasKeyboardFocus,
                    "activation-state-timeout");
            // Avoid a hot polling loop; wait only while the requested state is absent.
            statePoll.Wait((int)Math.Min(8, remaining));
        }
    }

    private static NativeAutomationException KeyboardActivationFailure(NodeEntry entry,
        nint previousRoot,
        bool activation,
        Stopwatch activationWatch,
        bool? hasKeyboardFocus,
        string stage)
    {
        var actual = GetForegroundWindow();
        var actualRoot = actual == 0 ? 0 : NativeWindows.GetAncestor(actual, 2);
        return new NativeAutomationException("foreground_changed",
            $"Input refused at {stage}: target HWND={entry.Root.Handle}, PID={entry.Root.ProcessId}; previous root HWND={previousRoot}; " +
            $"actual HWND={actual}, root HWND={actualRoot}, PID={NativeWindows.ProcessId(actual)}; SetForegroundWindow={activation}; " +
            $"elapsed={activationWatch.ElapsedMilliseconds}ms; HasKeyboardFocus={hasKeyboardFocus?.ToString() ?? "not-read"}.");
    }

    private void EnsureForeground(NodeEntry entry, bool? activation = null, string stage = "before-injection")
    {
        ValidateTarget();
        ValidateLifetimes(entry);
        var actual = GetForegroundWindow();
        var actualRoot = actual == 0 ? 0 : NativeWindows.GetAncestor(actual, 2);
        if (actualRoot != entry.Root.Handle || NativeWindows.ProcessId(actual) != entry.Root.ProcessId ||
            !IsScopedWindow(entry.Root.Handle))
        {
            throw new NativeAutomationException("foreground_changed",
                $"Input refused at {stage}: target HWND={entry.Root.Handle}, PID={entry.Root.ProcessId}; actual HWND={actual}, root HWND={actualRoot}, PID={NativeWindows.ProcessId(actual)}; SetForegroundWindow={activation}.");
        }
    }

    private static void EnsureNoPhysicalInput()
    {
        foreach (var key in new[] { 1, 2, 4, 5, 6, 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                throw new NativeAutomationException("input_busy",
                    "A mouse button or modifier is already held. Retry after the current physical input is released.");
    }

    private static ScreenPoint Point(NodeEntry entry, double? localX, double? localY)
    {
        var bounds = entry.Element.Properties.BoundingRectangle.Value;
        var x = localX ?? bounds.Width / 2d;
        var y = localY ?? bounds.Height / 2d;
        if (bounds.Width <= 0 || bounds.Height <= 0 || !double.IsFinite(x) || !double.IsFinite(y) ||
            x < 0 || y < 0 || x >= bounds.Width || y >= bounds.Height)
            throw new NativeAutomationException("invalid_coordinates",
                "Input coordinates must lie within the scoped element's current bounds.");
        return new ScreenPoint(checked((int)Math.Round(bounds.X + x)), checked((int)Math.Round(bounds.Y + y)));
    }

    private static void ValidatePoint(NodeEntry entry, ScreenPoint point, bool pointerMoved = false)
    {
        var window = NativeCoordinateSpace.WindowAt(point.X, point.Y);
        if (window == 0 || NativeWindows.GetAncestor(window, 2) != entry.Root.Handle)
            throw new NativeAutomationException("input_obscured",
                "The target point is covered by a different window; " +
                (pointerMoved
                    ? "the cursor may have moved, but no button or key events were injected."
                    : "no input was injected."));
    }

    private static ushort[] ParseKeys(string[] keys) => keys.Select(ParseKey).Distinct().ToArray();

    private static ushort ParseKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new NativeAutomationException("invalid_key", "A key name is required.");
        var name = value.ToUpperInvariant();
        if (name.Length == 1 && (name[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
            return name[0];
        if (name.StartsWith('F') && int.TryParse(name.AsSpan(1), out var function) && function is >= 1 and <= 24)
            return (ushort)(0x70 + function - 1);
        return name switch
        {
            "SHIFT" => 0x10, "CTRL" or "CONTROL" => 0x11, "ALT" => 0x12,
            "BACKSPACE" or "BACK" => 0x08, "TAB" => 0x09, "ENTER" or "RETURN" => 0x0D,
            "ESCAPE" or "ESC" => 0x1B, "SPACE" => 0x20, "PAGEUP" => 0x21, "PAGEDOWN" => 0x22,
            "END" => 0x23, "HOME" => 0x24, "LEFT" or "ARROWLEFT" => 0x25, "UP" or "ARROWUP" => 0x26,
            "RIGHT" or "ARROWRIGHT" => 0x27, "DOWN" or "ARROWDOWN" => 0x28,
            "INSERT" => 0x2D, "DELETE" or "DEL" => 0x2E,
            "OEMPLUS" => 0xBB, "OEMMINUS" => 0xBD, "OEMCOMMA" => 0xBC, "OEMPERIOD" => 0xBE,
            _ => throw new NativeAutomationException("invalid_key",
                $"Unsupported native key '{value}'. Use letters, digits, F1..F24, or a named editing/navigation key.")
        };
    }

    private static OsInput Key(ushort key, bool up) => new()
    {
        Type = 1, Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = key,
                Flags = (up ? 0x0002u : 0) | (key is >= 0x21 and <= 0x2E ? 0x0001u : 0)
            }
        }
    };

    private static OsInput Unicode(char character, bool up) => new()
    {
        Type = 1,
        Data = new InputUnion
            { Keyboard = new KeyboardInput { ScanCode = character, Flags = 0x0004u | (up ? 0x0002u : 0) } }
    };

    private static OsInput Mouse(uint flags, uint data = 0) => new()
        { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = data } } };

    private static OsInput Move(ScreenPoint point)
    {
        var transform = NativeCoordinateSpace.Normalize(point.X, point.Y);
        var input = new OsInput
        {
            Data = new InputUnion
            {
                Mouse = new MouseInput
                {
                    X = transform.X, Y = transform.Y,
                    Flags = 0x0001 | 0x4000 | 0x8000
                }
            }
        };
        return input;
    }

    private static uint InsertInputs(OsInput[] inputs)
    {
        using var coordinates = NativeCoordinateSpace.Enter();
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<OsInput>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ScreenPoint(int X, int Y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, OsInput[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint window,
        uint message,
        nint wParam,
        nint lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nuint result);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
