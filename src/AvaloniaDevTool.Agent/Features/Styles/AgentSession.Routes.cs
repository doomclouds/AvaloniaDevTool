using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterStylesMethods()
    {
        Map<OfficialStylesRequest>(OfficialPropertyMethods.Styles, request => GetOfficialStyles(request));
        Map<ObjectRequest>(InspectionMethods.Styles,
            request => Avalonia12DiagnosticsAdapter.Inspect(GetAvaloniaObject(request.ObjectId), _objects.GetId));
    }
}
