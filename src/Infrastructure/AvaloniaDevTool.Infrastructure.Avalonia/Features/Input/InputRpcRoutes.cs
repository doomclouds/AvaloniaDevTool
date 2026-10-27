using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class InputRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, InputInspectionService input)
    {
        routes.Map<InputRequest>(InputMethods.Send, request => input.Send(request));
        routes.Map<ClickInputRequest>(InputMethods.Click, request => input.Click(request));
        routes.Map<KeyChordRequest>(InputMethods.Chord, request => input.Chord(request));
        routes.Map<DragInputRequest>(InputMethods.Drag, request => input.Drag(request));
        routes.Map<TextSelectionRequest>(InputMethods.TextSelection, request => input.SelectText(request));
        routes.Map<SelectItemRequest>(InputMethods.SelectItem, request => input.SelectItem(request));
        routes.Map<ControlActionRequest>(InputMethods.ControlAction, request => input.PerformAction(request));
    }
}
