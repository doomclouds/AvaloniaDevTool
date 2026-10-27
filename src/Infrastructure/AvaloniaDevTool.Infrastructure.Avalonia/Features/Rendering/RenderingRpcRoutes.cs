using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class RenderingRpcRoutes
{
    internal static void Register(RpcMethodRegistry routes, RendererDiagnosticsService renderer)
    {
        routes.Map(RendererDiagnosticsMethods.Status, () => renderer.Status());
        routes.Map<RendererDiagnosticsSetRequest>(RendererDiagnosticsMethods.Set,
            request => renderer.Set(request));
        routes.Map(RendererDiagnosticsMethods.Stop, () => renderer.Stop());
    }
}
