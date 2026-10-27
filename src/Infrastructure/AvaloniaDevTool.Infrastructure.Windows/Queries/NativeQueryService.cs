using AvaloniaDevTool.NativeAutomation;
using static AvaloniaDevTool.NativeAutomation.Worker.NativeTargetRuntime;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal sealed class NativeQueryService(NativeTargetRuntime runtime)
{
    private readonly NativeTargetRuntime _runtime = runtime;

    internal NativeElementInfo Inspect(string nodeId) => Snapshot(_runtime.Resolve(nodeId), null, 0);

    internal NativeWindowInfo[] GetWindows()
    {
        var result = new List<NativeWindowInfo>();
        foreach (var entry in _runtime.Roots())
        {
            try
            {
                _runtime.ValidateEntry(entry);
                var info = WindowInfo(entry);
                _runtime.ValidateLifetimes(entry);
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
        _runtime.ValidateLifetimes(entry);
        return result;
    }

    private static string Text(string? text) => text is null ? "" : text.Length <= 4096 ? text : text[..4096];

    internal NativeTreeResult GetTree(NativeTreeQuery query)
    {
        var nodes = Walk(query.RootNodeId, query.MaxDepth, query.MaxNodes, out var truncated);
        return new NativeTreeResult(nodes, truncated, nodes.Length);
    }

    internal NativeFindResult Find(NativeFindQuery query)
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
        var roots = rootNodeId is null ? _runtime.Roots() : [_runtime.Resolve(rootNodeId)];
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
                _runtime.ValidateLifetimes(item.Entry);
                if (item.Siblings && followedSiblings.Add(item.Entry.Id))
                {
                    var sibling = _runtime.Walker.GetNextSibling(item.Entry.Element);
                    if (sibling is not null && sibling.Properties.ProcessId.Value == item.Entry.Root.ProcessId)
                        pending.Push((_runtime.Register(sibling, item.Entry.Root), item.Parent, item.Depth, true));
                }

                // A top-level owned window can also occur in its owner's UIA tree.
                // Deduplicate its subtree without dropping the owner's later children.
                if (!seen.Add(item.Entry.Id))
                    continue;
                result.Add(Snapshot(item.Entry, item.Parent, item.Depth));
                var child = _runtime.Walker.GetFirstChild(item.Entry.Element);
                if (child is null)
                    continue;
                if (item.Depth >= maxDepth)
                {
                    truncated = true;
                    continue;
                }

                if (child.Properties.ProcessId.Value == item.Entry.Root.ProcessId)
                    pending.Push((_runtime.Register(child, item.Entry.Root), item.Entry.Id, item.Depth + 1, true));
            }
            catch (Exception exception) when (item.Parent is not null &&
                                              NativeErrorMapper.Map(exception).Code is "stale_node" or "out_of_scope")
            {
                truncated = true;
            }
        }

        return result.ToArray();
    }
}
