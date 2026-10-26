using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private void RegisterRenderingMethods()
    {
        Map(RendererDiagnosticsMethods.Status, () => _rendererDiagnostics.Status());
        Map<RendererDiagnosticsSetRequest>(RendererDiagnosticsMethods.Set,
            request => _rendererDiagnostics.Set(request));
        Map(RendererDiagnosticsMethods.Stop, () => _rendererDiagnostics.Stop());
    }
}
