using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class RuntimeRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, RuntimeInspectionService runtime)
    {
        routes.Map<RuntimeMembersRequest>(RuntimeInspectionMethods.Members, request => runtime.GetMembers(request));
        routes.Map<RuntimeItemsRequest>(RuntimeInspectionMethods.Items, request => runtime.GetItems(request));
    }
}
