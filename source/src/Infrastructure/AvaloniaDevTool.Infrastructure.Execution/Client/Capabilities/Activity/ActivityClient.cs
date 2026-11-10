using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class ActivityClient
{
    private readonly DiagnosticsRpcContext context;

    internal ActivityClient(DiagnosticsRpcContext context)
    {
        this.context = context;
        context.Subscribe((method, payload) =>
        {
            switch (method)
            {
                case ActivityMethods.EntryAdded:
                    ActivityEntryAdded?.Invoke(ProtocolJson.Read<ActivityEntry>(payload));
                    return true;
                case ActivityMethods.StateChanged:
                    ActivityStateChanged?.Invoke(ProtocolJson.Read<ActivityStatus>(payload));
                    return true;
                case EventRouteMethods.RouteUpdated:
                    EventRouteUpdated?.Invoke(ProtocolJson.Read<EventRouteInfo>(payload));
                    return true;
                case EventRouteMethods.StateChanged:
                    EventRouteStateChanged?.Invoke(ProtocolJson.Read<EventRouteStatus>(payload));
                    return true;
                default:
                    return false;
            }
        });
    }

    public event Action<ActivityEntry>? ActivityEntryAdded;
    public event Action<ActivityStatus>? ActivityStateChanged;

    public async Task StartActivityAsync(ActivityStartRequest request, CancellationToken cancellationToken = default) =>
        _ = await context.InvokeAsync<OperationResult>(ActivityMethods.Start, request, cancellationToken)
            .ConfigureAwait(false);

    public async Task StopActivityAsync(CancellationToken cancellationToken = default) => _ =
        await context.InvokeAsync<OperationResult>(ActivityMethods.Stop, null, cancellationToken).ConfigureAwait(false);

    public async Task ClearActivityAsync(CancellationToken cancellationToken = default) => _ =
        await context.InvokeAsync<OperationResult>(ActivityMethods.Clear, null, cancellationToken)
            .ConfigureAwait(false);

    public Task<IReadOnlyList<ActivityEntry>> GetActivityAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<ActivityEntry>>(ActivityMethods.Snapshot, null, cancellationToken);

    public Task<ActivityStatus> GetActivityStatusAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ActivityStatus>(ActivityMethods.Status, null, cancellationToken);

    public event Action<EventRouteInfo>? EventRouteUpdated;
    public event Action<EventRouteStatus>? EventRouteStateChanged;

    public Task<EventCatalog> GetEventCatalogAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<EventCatalog>(EventRouteMethods.Catalog, null, cancellationToken);

    public Task<IReadOnlyList<EventRouteInfo>> GetEventRoutesAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<EventRouteInfo>>(EventRouteMethods.Snapshot, null, cancellationToken);

    public Task<EventRouteStatus> GetEventRouteStatusAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<EventRouteStatus>(EventRouteMethods.Status, null, cancellationToken);
}
