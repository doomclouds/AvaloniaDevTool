using System.Text.Json;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private readonly Dictionary<string, Func<JsonElement, object>> _syncMethods = new(StringComparer.Ordinal);

    private void RegisterSyncMethods()
    {
        RegisterSessionMethods();
        RegisterTreeMethods();
        RegisterPropertiesMethods();
        RegisterStylesMethods();
        RegisterSelectionMethods();
        RegisterOverlaysMethods();
        RegisterRenderingMethods();
        RegisterPopupsMethods();
        RegisterResourcesMethods();
        RegisterAssetsMethods();
        RegisterActivityMethods();
        RegisterMetricsMethods();
        RegisterProfilerMethods();
        RegisterBreakpointsMethods();
        RegisterLayoutMethods();
        RegisterRuntimeMethods();
        RegisterInputMethods();
    }

    private void RegisterSessionMethods()
    {
        Map(Methods.SessionInfo,
            () => _info with { ContentGeneration = _options.ContentGenerationProvider?.Invoke() ?? 0 });
        Map(PreviewMethods.ContentGeneration, () => _options.ContentGenerationProvider?.Invoke() ?? 0);
    }

    private void Map<TRequest>(string method, Func<TRequest, object> handler) =>
        _syncMethods.Add(method, parameters => handler(ProtocolJson.Read<TRequest>(parameters)));

    private void Map(string method, Func<object> handler) =>
        _syncMethods.Add(method, _ => handler());
}
