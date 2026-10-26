using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AvaloniaDevTool.NativeAutomation;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal sealed partial class NativeAutomationBackend
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NativeTargetIdentity _target;
    private readonly string _sessionId;
    private readonly UIA3Automation _automation;
    private readonly ITreeWalker _walker;
    private readonly NativeWindowLifetimeMonitor _lifetimes;
    private readonly ConcurrentDictionary<string, NodeEntry> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nodeKeys = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<NativeActionResult>> _operations = new(StringComparer.Ordinal);

    internal NativeAutomationBackend(NativeWorkerAttach attach)
    {
        _target = attach.Target;
        _sessionId = attach.SessionId;
        ValidateTarget();
        _lifetimes = new NativeWindowLifetimeMonitor(_target.ProcessId);
        _automation = new UIA3Automation
        {
            ConnectionTimeout = TimeSpan.FromSeconds(2),
            TransactionTimeout = TimeSpan.FromSeconds(3)
        };
        _walker = _automation.TreeWalkerFactory.GetControlViewWalker();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                try
                {
                    ValidateTarget();
                    if (attach.ParentProcessId > 0 && attach.ParentStartTimeUtc is { } parentStart &&
                        ProcessStartTicks(attach.ParentProcessId) != parentStart.UtcTicks)
                        Environment.Exit(0);
                }
                catch
                {
                    Environment.Exit(0);
                }
            }
        });
    }

    internal object Dispatch(string command, JsonElement payload)
    {
        ValidateTarget();
        return command switch
        {
            "windows" => GetWindows(),
            "tree" => GetTree(payload.Deserialize<NativeTreeQuery>(JsonOptions)!),
            "find" => Find(payload.Deserialize<NativeFindQuery>(JsonOptions)!),
            "element" => Snapshot(Resolve(payload.GetString()!), null, 0),
            "action" => Execute(payload.Deserialize<NativeActionRequest>(JsonOptions)!),
            "action-status" => ActionStatus(payload.GetString()!),
            "input" => Input(payload.Deserialize<NativeInputRequest>(JsonOptions)!),
            "screenshot" => Capture(payload.Deserialize<NativeScreenshotRequest>(JsonOptions)!),
            _ => throw new NativeAutomationException("unknown_command", $"Unknown native command '{command}'.")
        };
    }

    private void ValidateTarget()
    {
        if (_target.ProcessId <= 0)
            throw new NativeAutomationException("invalid_target", "A valid attached process ID is required.");
        if (ProcessStartTicks(_target.ProcessId) != _target.StartTimeUtc.UtcTicks)
            throw new NativeAutomationException("target_changed",
                "The attached process exited or its process ID was reused.");
    }

    private static long ProcessStartTicks(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
                throw new NativeAutomationException("target_exited", "The target process exited.");
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (ArgumentException exception)
        {
            throw new NativeAutomationException("target_exited", "The target process exited.", exception);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            throw new NativeAutomationException("access_denied",
                "Cannot verify the target process identity.",
                exception);
        }
    }

    private bool IsScopedWindow(nint handle)
    {
        if (!NativeWindows.IsWindow(handle))
            return false;
        var current = handle;
        for (var depth = 0; current != 0 && depth < 32; depth++)
        {
            if (NativeWindows.ProcessId(current) == _target.ProcessId)
                return true;
            current = NativeWindows.Owner(current);
        }

        return false;
    }

    private NodeEntry[] Roots()
    {
        var handles = new List<nint>();
        NativeWindows.EnumWindows((handle, _) =>
            {
                if (NativeWindows.IsWindowVisible(handle) && IsScopedWindow(handle))
                    handles.Add(handle);
                return handles.Count <= 256;
            },
            0);
        if (handles.Count > 256)
            throw new NativeAutomationException("window_limit", "The target has more than 256 native windows.");
        var roots = new List<NodeEntry>();
        foreach (var handle in handles)
        {
            if (!NativeWindows.IsWindowVisible(handle) || !IsScopedWindow(handle))
                continue;
            try
            {
                var processId = NativeWindows.ProcessId(handle);
                if (processId <= 0 || !IsScopedWindow(handle))
                    continue;
                var lifetime = _lifetimes.Capture(handle, processId);
                var element = _automation.FromHandle(handle);
                var runtimeId = RuntimeKey(element);
                var root = new RootIdentity(handle, processId, ProcessStartTicks(processId), runtimeId, lifetime);
                _lifetimes.Validate(lifetime);
                roots.Add(Register(element, root));
            }
            catch (FlaUI.Core.Exceptions.ElementNotAvailableException)
            {
            }
            catch (System.Runtime.InteropServices.COMException exception) when (exception.HResult ==
                                                                                unchecked((int)0x80040201))
            {
            }
            catch (NativeAutomationException exception) when (exception.Code is "target_exited" or "out_of_scope"
                                                                  or "stale_node")
            {
            }
        }

        return roots.ToArray();
    }

    private NativeWindowInfo[] GetWindows()
    {
        var result = new List<NativeWindowInfo>();
        foreach (var entry in Roots())
        {
            try
            {
                ValidateEntry(entry);
                var info = WindowInfo(entry);
                ValidateLifetimes(entry);
                result.Add(info);
            }
            catch (FlaUI.Core.Exceptions.ElementNotAvailableException)
            {
            }
            catch (System.Runtime.InteropServices.COMException exception) when (exception.HResult ==
                                                                                unchecked((int)0x80040201))
            {
            }
            catch (NativeAutomationException exception) when (exception.Code is "stale_node" or "out_of_scope")
            {
            }
        }

        return result.ToArray();
    }

    private static NativeWindowInfo WindowInfo(NodeEntry entry)
    {
        var element = entry.Element;
        var pattern = element.Patterns.Window.PatternOrDefault;
        return new NativeWindowInfo
        {
            NodeId = entry.Id,
            Handle = entry.Root.Handle.ToInt64(),
            OwnerHandle = NativeWindows.Owner(entry.Root.Handle).ToInt64(),
            ProcessId = entry.Root.ProcessId,
            Title = Text(element.Properties.Name.ValueOrDefault),
            ClassName = Text(element.Properties.ClassName.ValueOrDefault),
            IsModal = pattern?.IsModal.ValueOrDefault ?? false,
            IsOwned = NativeWindows.Owner(entry.Root.Handle) != 0,
            // Some providers expose the control's managed enabled state even when
            // Windows disables its HWND while an owned modal window is open.
            IsEnabled = element.Properties.IsEnabled.ValueOrDefault && NativeWindows.IsWindowEnabled(entry.Root.Handle),
            IsVisible = NativeWindows.IsWindowVisible(entry.Root.Handle),
            WindowVisualState = pattern?.WindowVisualState.ValueOrDefault.ToString()
        };
    }

    private NodeEntry Register(AutomationElement element, RootIdentity root)
    {
        _lifetimes.Validate(root.Lifetime);
        var elementHandle = element.Properties.NativeWindowHandle.ValueOrDefault;
        var actualRoot = elementHandle == 0 ? 0 : NativeWindows.GetAncestor(elementHandle, 2);
        if (actualRoot != 0 && actualRoot != root.Handle && IsScopedWindow(actualRoot))
        {
            var rootProcessId = NativeWindows.ProcessId(actualRoot);
            var rootLifetime = _lifetimes.Capture(actualRoot, rootProcessId);
            root = new RootIdentity(actualRoot,
                rootProcessId,
                ProcessStartTicks(rootProcessId),
                RuntimeKey(_automation.FromHandle(actualRoot)),
                rootLifetime);
        }

        var processId = element.Properties.ProcessId.Value;
        if (processId != root.ProcessId)
            throw new NativeAutomationException("out_of_scope",
                "The native element belongs to a process outside the attached window scope.");
        NativeWindowLifetimeMonitor.Stamp? lifetime =
            elementHandle == 0 ? null : _lifetimes.Capture(elementHandle, processId);
        var runtimeId = RuntimeKey(element);
        _lifetimes.Validate(root.Lifetime);
        if (lifetime is { } captured)
            _lifetimes.Validate(captured);
        // Runtime IDs and HWNDs can both repeat after destruction. A delivered
        // lifetime change must never replace the wrapper behind an older node ID.
        var key =
            $"{root.Handle}:{root.ProcessId}:{root.StartTicks}:{root.Lifetime.Epoch}:{root.RuntimeKey}:{runtimeId}:{elementHandle}:{lifetime?.Epoch}";
        NodeEntry entry;
        if (_nodeKeys.TryGetValue(key, out var existing) && _nodes.TryGetValue(existing, out var previous))
        {
            try
            {
                ValidateLifetimes(previous);
                // RuntimeId may be cached by UIA. Require a live property request
                // before refreshing a wrapper, including elements without HWNDs.
                _ = previous.Element.Properties.Name.Value;
                ValidateLifetimes(previous);
                entry = new NodeEntry(existing, element, root, runtimeId, lifetime);
                _nodes[existing] = entry;
                return entry;
            }
            catch (Exception exception) when (IsStale(exception))
            {
                _nodes.TryRemove(existing, out _);
                _nodeKeys.Remove(key);
            }
        }

        if (_nodeKeys.Count >= 20000)
            throw new NativeAutomationException("node_limit",
                "The native session reached its node identity limit; attach again.");
        var id = $"native:{_sessionId}:{Guid.NewGuid():N}";
        entry = new NodeEntry(id, element, root, runtimeId, lifetime);
        _nodes[id] = entry;
        _nodeKeys[key] = id;
        return entry;
    }

    private static string RuntimeKey(AutomationElement element) => string.Join('.', element.Properties.RuntimeId.Value);

    private NodeEntry Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith($"native:{_sessionId}:", StringComparison.Ordinal) ||
            !_nodes.TryGetValue(id, out var entry))
            throw new NativeAutomationException("stale_node",
                "The native node ID is unknown or belongs to another session. Enumerate the native tree again.");
        ValidateEntry(entry);
        return entry;
    }

    private void ValidateEntry(NodeEntry entry)
    {
        try
        {
            ValidateEntryCore(entry);
            ValidateLifetimes(entry);
        }
        catch (Exception exception) when (!NativeWindows.IsWindow(entry.Root.Handle) &&
                                          (exception is TimeoutException ||
                                           exception.HResult == unchecked((int)0x80131505)))
        {
            _nodes.TryRemove(entry.Id, out _);
            throw new NativeAutomationException("stale_node",
                "The scoped window was destroyed during native element validation.",
                exception);
        }
        catch (Exception exception) when (IsStale(exception))
        {
            // A confirmed stale ID stays retired even if a provider later reuses
            // its runtime identifier. Registration will allocate a fresh ID.
            _nodes.TryRemove(entry.Id, out _);
            throw;
        }
    }

    private static bool IsStale(Exception exception) =>
        exception is FlaUI.Core.Exceptions.ElementNotAvailableException ||
        exception is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x80040201) } ||
        exception is NativeAutomationException { Code: "stale_node" or "out_of_scope" };

    private void ValidateLifetimes(NodeEntry entry)
    {
        _lifetimes.Validate(entry.Root.Lifetime);
        if (entry.Lifetime is { } lifetime)
            _lifetimes.Validate(lifetime);
    }

    private void ValidateEntryCore(NodeEntry entry)
    {
        ValidateTarget();
        var root = entry.Root;
        ValidateLifetimes(entry);
        if (!IsScopedWindow(root.Handle) || NativeWindows.ProcessId(root.Handle) != root.ProcessId ||
            ProcessStartTicks(root.ProcessId) != root.StartTicks)
            throw new NativeAutomationException("out_of_scope",
                "The native window no longer belongs to the attached target.");
        var rootRuntime = RuntimeKey(_automation.FromHandle(root.Handle));
        if (rootRuntime != root.RuntimeKey || RuntimeKey(entry.Element) != entry.RuntimeKey)
            throw new NativeAutomationException("stale_node", "The native element or its owning window was replaced.");
        if (entry.Element.Properties.ProcessId.Value != root.ProcessId)
            throw new NativeAutomationException("out_of_scope", "The native element changed process ownership.");
        var nativeHandle = entry.Element.Properties.NativeWindowHandle.ValueOrDefault;
        if (nativeHandle != 0 && (NativeWindows.ProcessId(nativeHandle) != root.ProcessId ||
                                  NativeWindows.GetAncestor(nativeHandle, 2) != root.Handle))
            throw new NativeAutomationException("out_of_scope",
                "The native element's HWND no longer belongs to its scoped window.");
        var current = entry.Element;
        for (var depth = 0; current is not null && depth < 128; depth++)
        {
            if (RuntimeKey(current) == root.RuntimeKey)
                return;
            current = _walker.GetParent(current);
        }

        throw new NativeAutomationException("stale_node",
            "The native element is no longer a descendant of its scoped window.");
    }

    private NativeElementInfo Snapshot(NodeEntry entry, string? parent, int depth)
    {
        var element = entry.Element;
        var properties = element.Properties;
        var password = properties.IsPassword.ValueOrDefault;
        var rectangle = properties.BoundingRectangle.ValueOrDefault;
        var value = element.Patterns.Value.PatternOrDefault;
        var result = new NativeElementInfo
        {
            NodeId = entry.Id, ParentNodeId = parent, Depth = depth,
            ProcessId = properties.ProcessId.Value,
            NativeWindowHandle = properties.NativeWindowHandle.ValueOrDefault.ToInt64(),
            AutomationId = Text(properties.AutomationId.ValueOrDefault), Name = Text(properties.Name.ValueOrDefault),
            ControlType = properties.ControlType.ValueOrDefault.ToString(),
            ClassName = Text(properties.ClassName.ValueOrDefault),
            FrameworkId = Text(properties.FrameworkId.ValueOrDefault), IsEnabled = properties.IsEnabled.ValueOrDefault,
            IsOffscreen = properties.IsOffscreen.ValueOrDefault, IsPassword = password,
            HasKeyboardFocus = properties.HasKeyboardFocus.ValueOrDefault,
            Bounds = new NativeBounds(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height),
            Value = !password && value is not null ? Text(value.Value.ValueOrDefault) : null,
            ToggleState = element.Patterns.Toggle.PatternOrDefault?.ToggleState.ValueOrDefault.ToString(),
            ExpandCollapseState = element.Patterns.ExpandCollapse.PatternOrDefault?.ExpandCollapseState.ValueOrDefault
                .ToString(),
            IsSelected = element.Patterns.SelectionItem.PatternOrDefault?.IsSelected.ValueOrDefault,
            WindowVisualState = element.Patterns.Window.PatternOrDefault?.WindowVisualState.ValueOrDefault.ToString(),
            SupportedPatterns = element.GetSupportedPatterns().Select(pattern => pattern.Name).ToArray()
        };
        ValidateLifetimes(entry);
        return result;
    }

    private static string Text(string? text) => text is null ? "" : text.Length <= 4096 ? text : text[..4096];

    private NativeTreeResult GetTree(NativeTreeQuery query)
    {
        var nodes = Walk(query.RootNodeId, query.MaxDepth, query.MaxNodes, out var truncated);
        return new NativeTreeResult(nodes, truncated, nodes.Length);
    }

    private NativeFindResult Find(NativeFindQuery query)
    {
        if (query.MaxResults is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(query.MaxResults));
        if (query.AutomationId is null && query.Name is null && query.ClassName is null && query.ControlType is null)
            throw new NativeAutomationException("invalid_argument",
                "Specify at least one exact native element filter.");
        var nodes = Walk(query.RootNodeId, query.MaxDepth, query.MaxNodes, out var truncated);
        var matches = nodes.Where(node =>
            (query.AutomationId is null || node.AutomationId == query.AutomationId) &&
            (query.Name is null || node.Name == query.Name) &&
            (query.ClassName is null || node.ClassName == query.ClassName) &&
            (query.ControlType is null ||
             string.Equals(node.ControlType, query.ControlType, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new NativeFindResult(matches.Take(query.MaxResults).ToArray(),
            truncated || matches.Length > query.MaxResults,
            nodes.Length);
    }

    private NativeElementInfo[] Walk(string? rootNodeId, int maxDepth, int maxNodes, out bool truncated)
    {
        if (maxDepth is < 0 or > 32 || maxNodes is < 1 or > 5000)
            throw new NativeAutomationException("invalid_argument",
                "MaxDepth must be 0..32 and MaxNodes must be 1..5000.");
        var result = new List<NativeElementInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var followedSiblings = new HashSet<string>(StringComparer.Ordinal);
        var roots = rootNodeId is null ? Roots() : [Resolve(rootNodeId)];
        var pending = new Stack<(NodeEntry Entry, string? Parent, int Depth, bool Siblings)>();
        foreach (var root in roots.Reverse())
            pending.Push((root, null, 0, false));
        truncated = false;
        while (pending.TryPop(out var item))
        {
            if (result.Count >= maxNodes)
            {
                truncated = true;
                break;
            }

            try
            {
                ValidateLifetimes(item.Entry);
                if (item.Siblings && followedSiblings.Add(item.Entry.Id))
                {
                    var sibling = _walker.GetNextSibling(item.Entry.Element);
                    if (sibling is not null && sibling.Properties.ProcessId.Value == item.Entry.Root.ProcessId)
                        pending.Push((Register(sibling, item.Entry.Root), item.Parent, item.Depth, true));
                }

                // A top-level owned window can also occur in its owner's UIA tree.
                // Deduplicate its subtree without dropping the owner's later children.
                if (!seen.Add(item.Entry.Id))
                    continue;
                result.Add(Snapshot(item.Entry, item.Parent, item.Depth));
                var child = _walker.GetFirstChild(item.Entry.Element);
                if (child is null)
                    continue;
                if (item.Depth >= maxDepth)
                {
                    truncated = true;
                    continue;
                }

                if (child.Properties.ProcessId.Value == item.Entry.Root.ProcessId)
                    pending.Push((Register(child, item.Entry.Root), item.Entry.Id, item.Depth + 1, true));
            }
            catch (Exception exception) when (item.Parent is not null &&
                                              Program.MapError(exception).Code is "stale_node" or "out_of_scope")
            {
                truncated = true;
            }
        }

        return result.ToArray();
    }

    private NativeActionResult Execute(NativeActionRequest request)
    {
        var entry = Resolve(request.NodeId);
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
        var operationId = $"native-operation:{_sessionId}:{Guid.NewGuid():N}";
        var operation = Task.Run(() =>
        {
            try
            {
                ValidateEntry(entry);
                Perform(entry.Element, request);
                return new NativeActionResult(request.NodeId, request.Action, "completed", operationId);
            }
            catch (Exception exception)
            {
                var error = Program.MapError(exception);
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

    private NativeActionResult ActionStatus(string operationId)
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

    private sealed record RootIdentity(
        nint Handle,
        int ProcessId,
        long StartTicks,
        string RuntimeKey,
        NativeWindowLifetimeMonitor.Stamp Lifetime);

    private sealed record NodeEntry(
        string Id,
        AutomationElement Element,
        RootIdentity Root,
        string RuntimeKey,
        NativeWindowLifetimeMonitor.Stamp? Lifetime);
}
