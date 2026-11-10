using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class BreakpointsRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, BreakpointService breakpoints)
    {
        routes.Map<CreateBreakpointRequest>(BreakpointMethods.Create, request => breakpoints.Create(request));
        routes.Map<UpdateBreakpointRequest>(BreakpointMethods.Update, request => breakpoints.Update(request));
        routes.Map<RemoveBreakpointRequest>(BreakpointMethods.Remove, request => breakpoints.Remove(request));
        routes.Map(BreakpointMethods.List, () => breakpoints.List());
        routes.Map(BreakpointMethods.History, () => breakpoints.History());
    }
}
