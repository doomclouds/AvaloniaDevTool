using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "events-catalog", ReadOnly = true), Description("Read the actual registered Avalonia routed events, stable registry identities, strategies and default event groups.")]
    public async Task<EventCatalog> EventCatalog(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).GetEventCatalogAsync(cancellationToken);

    [McpServerTool(Name = "events-start"), Description("Capture exact registered event identities and/or event groups into correlated route entities. Without an explicit selection, uses the six configured default groups. Observations are before instance handlers; Handled changes describe intervals, not attribution to a specific handler.")]
    public async Task<ActivityStatus> StartEventRoutes(string[]? eventIds = null, string[]? groups = null,
        bool includeAll = false, long? sourceNodeId = null, CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        await client.StartActivityAsync(new ActivityStartRequest(false, true, sourceNodeId,
            EventSelection: new EventSelection(eventIds, groups, includeAll)), cancellationToken);
        return await client.GetActivityStatusAsync(cancellationToken);
    }

    [McpServerTool(Name = "events-routes", ReadOnly = true), Description("Read bounded correlated event routes with tunnel/bubble/direct observations, strategy finishes, weak source/receiver identities and explicit truncation/expiry.")]
    public async Task<IReadOnlyList<EventRouteInfo>> EventRoutes(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).GetEventRoutesAsync(cancellationToken);

    [McpServerTool(Name = "events-status", ReadOnly = true), Description("Read event-route count/byte/TTL limits and drop/truncation counters. Use activity-stop to stop while retaining history.")]
    public async Task<EventRouteStatus> EventRouteStatus(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).GetEventRouteStatusAsync(cancellationToken);
}
