using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli.Catalog;

/// <summary>The single execution contract, shared by the local gateway and the HTTP executor.</summary>
public static class McpToolCatalog
{
    public static McpServerTool[] CreateTools(ToolSession session, ManagedToolContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        context ??= new ManagedToolContext(session);
        var tools = new List<McpServerTool>();
        foreach (var capability in CapabilityToolRegistration.Create(session, context))
        foreach (var method in capability.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                     .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null))
        {
            McpServerTool tool = McpServerTool.Create(method, capability);
            if (capability is TargetToolBase)
                tool = new Gateway.ManagedIdentityTool(tool, context);
            tools.Add(new Gateway.ExecutionScopeTool(tool, session));
        }

        if (tools.Select(tool => tool.ProtocolTool.Name).Distinct(StringComparer.Ordinal).Count() != tools.Count)
            throw new InvalidOperationException("Tool names must be unique in the execution catalog.");
        return tools.ToArray();
    }

    public static void Configure(McpServerOptions options, IReadOnlyList<McpServerTool> tools)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tools);
        // A timed-out SDK 2.3 discovery can send initialize after committing the connection to 2026.
        // Offer the verified session-capable contract explicitly before accepting discovery metadata.
        options.ProtocolVersion = ExecutionProtocol.Version;
        options.ToolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in tools)
            options.ToolCollection.Add(tool);
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Tools ??= new ToolsCapability();
        var schemas = tools.ToDictionary(tool => tool.ProtocolTool.Name,
            tool => tool.ProtocolTool.InputSchema,
            StringComparer.Ordinal);
        options.Filters ??= new McpServerFilters();
        options.Filters.Request ??= new McpRequestFilters();
        options.Filters.Request.CallToolFilters ??= [];
        options.Filters.Request.CallToolFilters.Add(next => async (request, token) =>
        {
            if (request.Params is { } parameters && schemas.TryGetValue(parameters.Name, out var schema))
                ToolArgumentValidation.Validate(parameters, schema);
            return await next(request, token).ConfigureAwait(false);
        });
    }
}
