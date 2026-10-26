using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private bool _picking;
    private bool _followFocus;

    private void RegisterSelectionMethods()
    {
        Map<HighlightRequest>(Methods.Highlight, request => SetHighlight(request.ObjectId));
        Map<PickingRequest>(Methods.SetPicking, request => SetPicking(request.Enabled));
        Map<FollowFocusRequest>(ExplorationMethods.FollowFocus, request => SetFollowFocus(request.Enabled));
    }

    private OperationResult SetHighlight(long? id)
    {
        ClearHighlight();
        if (id is not null)
        {
            if (_objects.Get(id.Value) is not Control control)
                throw new RpcException("unsupported_object", "Only controls can be highlighted.");
            _overlay.Show(control);
        }

        return new OperationResult();
    }

    private void ClearHighlight()
    {
        _overlay.Clear();
    }

    private OperationResult SetPicking(bool enabled)
    {
        _picking = enabled;
        if (!enabled)
            ClearHighlight();
        RefreshRoots();
        Notify(Methods.PickingChanged, new PickingChangedNotification(_picking));
        return new OperationResult();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_picking && e.Source is Control control && !OverlayService.IsDiagnosticVisual(control))
        {
            var current = _overlay.CurrentTarget;
            if (current != control)
            {
                ClearHighlight();
                _overlay.Show(control);
            }
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_picking || e.Source is not Control control || OverlayService.IsDiagnosticVisual(control))
            return;
        e.Handled = true;
        _picking = false;
        var id = Observe(control);
        SetHighlight(id);
        Notify(Methods.SelectionChanged, new SelectionChangedNotification(id));
        Notify(Methods.PickingChanged, new PickingChangedNotification(false));
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_picking && e.Key == Key.Escape)
        {
            SetPicking(false);
            e.Handled = true;
        }
    }
}
