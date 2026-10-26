using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
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
}
