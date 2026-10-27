using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class ProfilerRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, ProfilerService profiler)
    {
        routes.Map<ProfilerStartRequest>(ProfilerMethods.Start, request => profiler.Start(request));
        routes.Map(ProfilerMethods.Stop, () => profiler.Stop());
        routes.Map(ProfilerMethods.Clear, () => profiler.Clear());
        routes.Map(ProfilerMethods.Snapshot, () => profiler.Snapshot());
        routes.Map(ProfilerMethods.Status, () => profiler.Status());
    }
}
