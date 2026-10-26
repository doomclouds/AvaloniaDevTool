using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterResourcesMethods()
    {
        Map<ResourcesRequest>(InspectionMethods.Resources, request => _resources.GetTrees(request));
        Map<ResourceEntriesRequest>(InspectionMethods.ResourceEntries,
            request => _resources.GetEntries(request.DictionaryId));
        Map<SetResourceRequest>(InspectionMethods.SetResource, request => _resources.Set(request));
        Map<ResetResourceRequest>(InspectionMethods.ResetResource, request => _resources.Reset(request));
        Map<ResourceTreeQueryRequest>(ResourceQueryMethods.Trees, request => _resources.GetTree(request));
        Map<ResourceEntriesQueryRequest>(ResourceQueryMethods.Entries, request => _resources.QueryEntries(request));
        Map<ResourceReadEntryRequest>(ResourceQueryMethods.ReadEntry, request => _resources.ReadEntry(request));
    }
}
