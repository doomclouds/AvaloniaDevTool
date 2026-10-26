using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterRuntimeMethods()
    {
        Map<RuntimeMembersRequest>(RuntimeInspectionMethods.Members, request => _runtime.GetMembers(request));
        Map<RuntimeItemsRequest>(RuntimeInspectionMethods.Items, request => _runtime.GetItems(request));
    }
}
