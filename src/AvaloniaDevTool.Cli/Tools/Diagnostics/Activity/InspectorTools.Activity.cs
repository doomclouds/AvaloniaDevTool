using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "activity-start"),
     Description(
         "Capture bounded Avalonia logs and/or routed events. Event filter defaults to Click; '*' enables all registered routed events.")]
    public async Task<ActivityStatus> StartActivity(bool logs = true,
        bool events = false,
        long? sourceNodeId = null,
        string eventName = "Click",
        string minimumLevel = "Warning",
        string? categoryFilter = null,
        string? messageFilter = null,
        string? parameterName = null,
        string? parameterValue = null,
        CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        await client.StartActivityAsync(new ActivityStartRequest(logs,
                events,
                sourceNodeId,
                eventName,
                minimumLevel,
                new LogFilter(categoryFilter, messageFilter, parameterName, parameterValue)),
            cancellationToken);
        return await client.GetActivityStatusAsync(cancellationToken);
    }

    [McpServerTool(Name = "activity-stop"),
     Description("Stop log/event capture and release listeners, retaining the bounded history.")]
    public async Task<ActivityStatus> StopActivity(CancellationToken cancellationToken)
    {
        var client = await session.GetAsync(cancellationToken);
        await client.StopActivityAsync(cancellationToken);
        return await client.GetActivityStatusAsync(cancellationToken);
    }

    [McpServerTool(Name = "activity", ReadOnly = true),
     Description("Read up to 500 captured log/event entries, including route, handled state and source object IDs.")]
    public async Task<IReadOnlyList<ActivityEntry>> Activity(CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).GetActivityAsync(cancellationToken);
}
