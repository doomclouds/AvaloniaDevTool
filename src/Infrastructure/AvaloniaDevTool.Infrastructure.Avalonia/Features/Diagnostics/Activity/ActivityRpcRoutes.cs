using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class ActivityRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, ActivityService activity)
    {
        routes.Map(EventRouteMethods.Catalog, () => activity.GetEventCatalog());
        routes.Map(EventRouteMethods.Snapshot, () => activity.EventRoutesSnapshot());
        routes.Map(EventRouteMethods.Status, () => activity.EventRoutesStatus());
        routes.Map<ActivityStartRequest>(ActivityMethods.Start, request => activity.Start(request));
        routes.Map(ActivityMethods.Stop, () => activity.Stop());
        routes.Map(ActivityMethods.Snapshot, () => activity.Snapshot());
        routes.Map(ActivityMethods.Clear, () => activity.Clear());
        routes.Map(ActivityMethods.Status, () => activity.Status());
    }
}
