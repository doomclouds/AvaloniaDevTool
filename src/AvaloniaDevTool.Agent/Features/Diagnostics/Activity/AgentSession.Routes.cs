using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterActivityMethods()
    {
        Map(EventRouteMethods.Catalog, () => _activity.GetEventCatalog());
        Map(EventRouteMethods.Snapshot, () => _activity.EventRoutesSnapshot());
        Map(EventRouteMethods.Status, () => _activity.EventRoutesStatus());
        Map<ActivityStartRequest>(ActivityMethods.Start, request => _activity.Start(request));
        Map(ActivityMethods.Stop, () => _activity.Stop());
        Map(ActivityMethods.Snapshot, () => _activity.Snapshot());
        Map(ActivityMethods.Clear, () => _activity.Clear());
        Map(ActivityMethods.Status, () => _activity.Status());
    }
}
