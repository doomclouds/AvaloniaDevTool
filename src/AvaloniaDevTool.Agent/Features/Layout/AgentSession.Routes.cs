using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterLayoutMethods()
    {
        Map<LayoutSnapshotRequest>(LayoutMethods.Snapshot, request => _layout.Snapshot(request));
    }
}
