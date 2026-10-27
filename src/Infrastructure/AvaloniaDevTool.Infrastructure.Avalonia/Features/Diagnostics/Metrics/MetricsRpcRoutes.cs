using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class MetricsRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, MetricsService metrics)
    {
        routes.Map<MetricsStartRequest>(MetricsMethods.Start, request => metrics.Start(request));
        routes.Map(MetricsMethods.Stop, () => metrics.Stop());
        routes.Map<MetricsClearRequest>(MetricsMethods.Clear, request => metrics.Clear(request));
        routes.Map(MetricsMethods.Snapshot, () => metrics.Snapshot());
        routes.Map(MetricsMethods.Status, () => metrics.Status());
    }
}
