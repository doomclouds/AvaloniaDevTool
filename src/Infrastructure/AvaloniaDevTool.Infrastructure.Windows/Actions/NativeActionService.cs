using System.Collections.Concurrent;
using AvaloniaDevTool.NativeAutomation;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal sealed class NativeActionService(NativeTargetRuntime runtime)
{
    private readonly NativeTargetRuntime _runtime = runtime;
    private readonly ConcurrentDictionary<string, Task<NativeActionResult>> _operations = new(StringComparer.Ordinal);

    internal NativeActionResult Execute(NativeActionRequest request)
    {
        var entry = _runtime.Resolve(request.NodeId);
        if (!NativeWindows.IsWindowEnabled(entry.Root.Handle) &&
            request.Action.ToLowerInvariant() is not ("close" or "minimize" or "maximize" or "restore"))
            throw new NativeAutomationException("element_disabled",
                "Windows has disabled the scoped window while an owned modal window is open.");
        if (request.Value?.Length > 32768)
            throw new NativeAutomationException("invalid_argument", "Native values are limited to 32768 characters.");
        if (request.Action.Equals("Close", StringComparison.OrdinalIgnoreCase) &&
            entry.Element.Properties.ControlType.ValueOrDefault == ControlType.Window &&
            entry.Element.Properties.NativeWindowHandle.ValueOrDefault == entry.Root.Handle)
        {
            Required(entry.Element.Patterns.Window.PatternOrDefault);
            if (!NativeWindows.PostMessage(entry.Root.Handle, 0x0010, 0, 0))
                throw new NativeAutomationException("dispatch_failed", "Windows rejected the scoped close message.");
            return new NativeActionResult(request.NodeId, request.Action, "dispatched");
        }

        if (request.Action.Equals("Invoke", StringComparison.OrdinalIgnoreCase))
        {
            var handle = entry.Element.Properties.NativeWindowHandle.ValueOrDefault;
            var className = NativeWindows.ClassName(handle);
            if (handle != 0 && entry.Element.Properties.ControlType.ValueOrDefault == ControlType.Button &&
                (className.Equals("Button", StringComparison.OrdinalIgnoreCase) ||
                 className.StartsWith("WindowsForms10.BUTTON.", StringComparison.OrdinalIgnoreCase)))
            {
                if (!entry.Element.Properties.IsEnabled.ValueOrDefault)
                    throw new NativeAutomationException("element_disabled", "The native button is disabled.");
                Required(entry.Element.Patterns.Invoke.PatternOrDefault);
                if (!NativeWindows.PostMessage(handle, 0x00F5, 0, 0))
                    throw new NativeAutomationException("dispatch_failed",
                        "Windows rejected the scoped button message.");
                // A native button may synchronously open a modal dialog. Posting its
                // standard click message avoids occupying the UIA provider connection.
                return new NativeActionResult(request.NodeId, request.Action, "dispatched");
            }
        }

        if (_operations.Count(pair => !pair.Value.IsCompleted) >= 8)
            throw new NativeAutomationException("operation_limit",
                "Eight native actions are still pending; inspect or dismiss their modal windows first.");
        // UIA providers may synchronously wait for a modal window to close. Keep the
        // request loop free so its owned prompt can be inspected and answered.
        var operationId = $"native-operation:{_runtime.SessionId}:{Guid.NewGuid():N}";
        var operation = Task.Run(() =>
        {
            try
            {
                _runtime.ValidateEntry(entry);
                Perform(entry.Element, request);
                return new NativeActionResult(request.NodeId, request.Action, "completed", operationId);
            }
            catch (Exception exception)
            {
                var error = NativeErrorMapper.Map(exception);
                return new NativeActionResult(request.NodeId,
                    request.Action,
                    "failed",
                    operationId,
                    error.Code,
                    error.Message);
            }
        });
        _operations[operationId] = operation;
        if (operation.Wait(50))
            return operation.Result;
        return new NativeActionResult(request.NodeId, request.Action, "pending", operationId);
    }

    internal NativeActionResult ActionStatus(string operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
            throw new NativeAutomationException("unknown_operation",
                "The operation ID is unknown or belongs to another native session.");
        return operation.IsCompleted ? operation.Result : new NativeActionResult("", "", "pending", operationId);
    }

    private static T Required<T>(T? pattern) where T : class => pattern
                                                                ?? throw new NativeAutomationException(
                                                                    "unsupported_pattern",
                                                                    "The native element does not support this action.");

    private static void Perform(AutomationElement element, NativeActionRequest request)
    {
        if (!element.Properties.IsEnabled.ValueOrDefault &&
            !request.Action.Equals("Close", StringComparison.OrdinalIgnoreCase))
            throw new NativeAutomationException("element_disabled", "The native element is disabled.");
        switch (request.Action.ToLowerInvariant())
        {
            case "invoke":
                Required(element.Patterns.Invoke.PatternOrDefault).Invoke();
                break;
            case "setvalue":
                var value = Required(element.Patterns.Value.PatternOrDefault);
                if (value.IsReadOnly.ValueOrDefault)
                    throw new NativeAutomationException("read_only", "The native value is read-only.");
                value.SetValue(request.Value ??
                               throw new NativeAutomationException("invalid_argument", "SetValue requires a value."));
                break;
            case "select":
                Required(element.Patterns.SelectionItem.PatternOrDefault).Select();
                break;
            case "addtoselection":
                Required(element.Patterns.SelectionItem.PatternOrDefault).AddToSelection();
                break;
            case "removefromselection":
                Required(element.Patterns.SelectionItem.PatternOrDefault).RemoveFromSelection();
                break;
            case "expand":
                Required(element.Patterns.ExpandCollapse.PatternOrDefault).Expand();
                break;
            case "collapse":
                Required(element.Patterns.ExpandCollapse.PatternOrDefault).Collapse();
                break;
            case "toggle":
                Required(element.Patterns.Toggle.PatternOrDefault).Toggle();
                break;
            case "focus":
                element.Focus();
                break;
            case "scrollintoview":
                Required(element.Patterns.ScrollItem.PatternOrDefault).ScrollIntoView();
                break;
            case "scroll":
                if (!Enum.TryParse<ScrollAmount>(request.HorizontalScroll, true, out var horizontal) ||
                    !Enum.IsDefined(horizontal) ||
                    !Enum.TryParse<ScrollAmount>(request.VerticalScroll, true, out var vertical) ||
                    !Enum.IsDefined(vertical))
                    throw new NativeAutomationException("invalid_argument",
                        "Scroll amount must be NoAmount, SmallIncrement, SmallDecrement, LargeIncrement, or LargeDecrement.");
                Required(element.Patterns.Scroll.PatternOrDefault).Scroll(horizontal, vertical);
                break;
            case "close":
                Required(element.Patterns.Window.PatternOrDefault).Close();
                break;
            case "minimize":
                Required(element.Patterns.Window.PatternOrDefault).SetWindowVisualState(WindowVisualState.Minimized);
                break;
            case "maximize":
                Required(element.Patterns.Window.PatternOrDefault).SetWindowVisualState(WindowVisualState.Maximized);
                break;
            case "restore":
                Required(element.Patterns.Window.PatternOrDefault).SetWindowVisualState(WindowVisualState.Normal);
                break;
            default:
                throw new NativeAutomationException("unsupported_action", $"Unknown native action '{request.Action}'.");
        }
    }
}
