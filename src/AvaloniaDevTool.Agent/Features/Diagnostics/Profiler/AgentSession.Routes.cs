using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterProfilerMethods()
    {
        Map<ProfilerStartRequest>(ProfilerMethods.Start, request => _profiler.Start(request));
        Map(ProfilerMethods.Stop, () => _profiler.Stop());
        Map(ProfilerMethods.Clear, () => _profiler.Clear());
        Map(ProfilerMethods.Snapshot, () => _profiler.Snapshot());
        Map(ProfilerMethods.Status, () => _profiler.Status());
    }
}
