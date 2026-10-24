using System.Text.Json;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public event Action<EventRouteInfo>? EventRouteUpdated;
    public event Action<EventRouteStatus>? EventRouteStateChanged;
    public Task<EventCatalog> GetEventCatalogAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<EventCatalog>(EventRouteMethods.Catalog, null, cancellationToken);
    public Task<IReadOnlyList<EventRouteInfo>> GetEventRoutesAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<EventRouteInfo>>(EventRouteMethods.Snapshot, null, cancellationToken);
    public Task<EventRouteStatus> GetEventRouteStatusAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<EventRouteStatus>(EventRouteMethods.Status, null, cancellationToken);

    private bool HandleEventRouteNotification(string method, JsonElement payload)
    {
        switch (method)
        {
            case EventRouteMethods.RouteUpdated: EventRouteUpdated?.Invoke(ProtocolJson.Read<EventRouteInfo>(payload)); return true;
            case EventRouteMethods.StateChanged: EventRouteStateChanged?.Invoke(ProtocolJson.Read<EventRouteStatus>(payload)); return true;
            default: return false;
        }
    }
}
