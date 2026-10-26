using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterBreakpointsMethods()
    {
        Map<CreateBreakpointRequest>(BreakpointMethods.Create, request => _breakpoints.Create(request));
        Map<UpdateBreakpointRequest>(BreakpointMethods.Update, request => _breakpoints.Update(request));
        Map<RemoveBreakpointRequest>(BreakpointMethods.Remove, request => _breakpoints.Remove(request));
        Map(BreakpointMethods.List, () => _breakpoints.List());
        Map(BreakpointMethods.History, () => _breakpoints.History());
    }
}
