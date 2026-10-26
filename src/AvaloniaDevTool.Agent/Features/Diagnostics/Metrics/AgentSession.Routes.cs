using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterMetricsMethods()
    {
        Map<MetricsStartRequest>(MetricsMethods.Start, request => _metrics.Start(request));
        Map(MetricsMethods.Stop, () => _metrics.Stop());
        Map<MetricsClearRequest>(MetricsMethods.Clear, request => _metrics.Clear(request));
        Map(MetricsMethods.Snapshot, () => _metrics.Snapshot());
        Map(MetricsMethods.Status, () => _metrics.Status());
    }
}
