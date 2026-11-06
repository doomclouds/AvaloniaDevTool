using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class ResourcesRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, ResourceInspectionService resources)
    {
        routes.Map<ResourcesRequest>(InspectionMethods.Resources, request => resources.GetTrees(request));
        routes.Map<ResourceEntriesRequest>(InspectionMethods.ResourceEntries,
            request => resources.GetEntries(request.DictionaryId));
        routes.Map<SetResourceRequest>(InspectionMethods.SetResource, request => resources.Set(request));
        routes.Map<ResetResourceRequest>(InspectionMethods.ResetResource, request => resources.Reset(request));
        routes.Map<ResourceTreeQueryRequest>(ResourceQueryMethods.Trees, request => resources.GetTree(request));
        routes.Map<ResourceEntriesQueryRequest>(ResourceQueryMethods.Entries,
            request => resources.QueryEntries(request));
        routes.Map<ResourceReadEntryRequest>(ResourceQueryMethods.ReadEntry, request => resources.ReadEntry(request));
    }
}
