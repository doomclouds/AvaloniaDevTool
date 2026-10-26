using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterPopupsMethods()
    {
        Map(PopupFreezeMethods.Status, () => _popupFreeze.Status());
        Map<PopupFreezeSetRequest>(PopupFreezeMethods.Set, request => _popupFreeze.Set(request));
        Map(PopupFreezeMethods.Resume, () => _popupFreeze.Resume());
    }
}
