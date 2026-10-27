using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class PopupsRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, PopupFreezeService popups)
    {
        routes.Map(PopupFreezeMethods.Status, () => popups.Status());
        routes.Map<PopupFreezeSetRequest>(PopupFreezeMethods.Set, request => popups.Set(request));
        routes.Map(PopupFreezeMethods.Resume, () => popups.Resume());
    }
}
