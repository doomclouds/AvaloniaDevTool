using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterInputMethods()
    {
        Map<InputRequest>(InputMethods.Send, request => _input.Send(request));
        Map<ClickInputRequest>(InputMethods.Click, request => _input.Click(request));
        Map<KeyChordRequest>(InputMethods.Chord, request => _input.Chord(request));
        Map<DragInputRequest>(InputMethods.Drag, request => _input.Drag(request));
        Map<TextSelectionRequest>(InputMethods.TextSelection, request => _input.SelectText(request));
        Map<SelectItemRequest>(InputMethods.SelectItem, request => _input.SelectItem(request));
        Map<ControlActionRequest>(InputMethods.ControlAction, request => _input.PerformAction(request));
    }
}
