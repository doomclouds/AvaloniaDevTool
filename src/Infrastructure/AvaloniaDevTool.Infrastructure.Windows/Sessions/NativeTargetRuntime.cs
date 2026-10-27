using System.Collections.Concurrent;
using System.Diagnostics;
using AvaloniaDevTool.NativeAutomation;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal sealed class NativeTargetRuntime
{
    private readonly NativeTargetIdentity _target;
    private readonly string _sessionId;
    private readonly UIA3Automation _automation;
    private readonly ITreeWalker _walker;
    private readonly NativeWindowLifetimeMonitor _lifetimes;
    private readonly ConcurrentDictionary<string, NodeEntry> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nodeKeys = new(StringComparer.Ordinal);

    internal string SessionId => _sessionId;
    internal ITreeWalker Walker => _walker;
    internal UIA3Automation Automation => _automation;

    internal NativeTargetRuntime(NativeWorkerAttach attach)
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

    internal void ValidateTarget()
    {
        if (_target.ProcessId <= 0)
            throw new NativeAutomationException("invalid_target", "A valid attached process ID is required.");
        if (ProcessStartTicks(_target.ProcessId) != _target.StartTimeUtc.UtcTicks)
            throw new NativeAutomationException("target_changed",
                "The attached process exited or its process ID was reused.");
    }

    internal static long ProcessStartTicks(int processId)
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

    internal bool IsScopedWindow(nint handle)
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

    internal NodeEntry[] Roots()
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

    internal NodeEntry Register(AutomationElement element, RootIdentity root)
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

    internal NodeEntry Resolve(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith($"native:{_sessionId}:", StringComparison.Ordinal) ||
            !_nodes.TryGetValue(id, out var entry))
            throw new NativeAutomationException("stale_node",
                "The native node ID is unknown or belongs to another session. Enumerate the native tree again.");
        ValidateEntry(entry);
        return entry;
    }

    internal void ValidateEntry(NodeEntry entry)
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

    internal void ValidateLifetimes(NodeEntry entry)
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

    internal sealed record RootIdentity(
        nint Handle,
        int ProcessId,
        long StartTicks,
        string RuntimeKey,
        NativeWindowLifetimeMonitor.Stamp Lifetime);

    internal sealed record NodeEntry(
        string Id,
        AutomationElement Element,
        RootIdentity Root,
        string RuntimeKey,
        NativeWindowLifetimeMonitor.Stamp? Lifetime);
}
