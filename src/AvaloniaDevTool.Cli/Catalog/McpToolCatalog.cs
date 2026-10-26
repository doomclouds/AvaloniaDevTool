using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli.Catalog;

public static class McpToolCatalog
{
    public static IMcpServerBuilder Register(IMcpServerBuilder builder,
        ToolSession session,
        string profile,
        ManagedToolContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var tools = CreateTools(session, profile, context);
        builder.WithTools(tools);
        builder.Services.Configure<McpServerOptions>(options => ConfigureFilters(options, tools, profile));
        return builder;
    }

    public static McpServerTool[] CreateTools(ToolSession session, string profile, ManagedToolContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ValidateProfile(profile);
        if (profile == "extended")
            return ReadTools(new InspectorTools(session));

        context ??= new ManagedToolContext(session);
        var official = new OfficialTools(session, context);
        var tools = ReadTools(official).Select(OfficialContracts.Declare);
        return profile == "agent"
            ? tools.Concat(ReadTools(new AgentTools(session, context))).ToArray()
            : tools.ToArray();
    }

    // The HTTP host calls this once with fresh options and tools owned by one MCP session.
    public static void Configure(McpServerOptions options, IReadOnlyList<McpServerTool> tools, string profile)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tools);
        ValidateProfile(profile);
        options.ToolCollection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in tools)
            options.ToolCollection.Add(tool);
        options.Capabilities ??= new ServerCapabilities();
        options.Capabilities.Tools ??= new ToolsCapability();
        ConfigureFilters(options, tools, profile);
    }

    private static McpServerTool[] ReadTools(object target) => target.GetType()
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
        .Select(method => McpServerTool.Create(method, target)).ToArray();

    private static void ConfigureFilters(McpServerOptions options, IReadOnlyList<McpServerTool> tools, string profile)
    {
        if (profile == "extended")
            return;
        var agentSchemas = tools.Where(tool => !OfficialTools.ToolNames.Contains(tool.ProtocolTool.Name))
            .ToDictionary(tool => tool.ProtocolTool.Name,
                tool => JsonSerializer.SerializeToElement(tool.ProtocolTool,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("inputSchema"),
                StringComparer.Ordinal);
        options.Filters ??= new McpServerFilters();
        options.Filters.Request ??= new McpRequestFilters();
        options.Filters.Request.CallToolFilters ??= [];
        options.Filters.Request.CallToolFilters.Add(next => async (request, token) =>
        {
            OfficialContracts.Validate(request.Params!);
            if (agentSchemas.TryGetValue(request.Params!.Name, out var schema))
                OfficialContracts.ValidateSchema(request.Params, schema);
            return await next(request, token).ConfigureAwait(false);
        });

        const string listMarker = "AvaloniaDevTool.OfficialToolsList";
        options.Filters.Message ??= new McpMessageFilters();
        options.Filters.Message.IncomingFilters ??= [];
        options.Filters.Message.OutgoingFilters ??= [];
        options.Filters.Message.IncomingFilters.Add(next => async (message, token) =>
        {
            if (message.JsonRpcMessage is JsonRpcRequest { Method: RequestMethods.ToolsList })
                message.Items[listMarker] = true;
            await next(message, token).ConfigureAwait(false);
        });
        options.Filters.Message.OutgoingFilters.Add(next => async (message, token) =>
        {
            if (message.Items.TryGetValue(listMarker, out var marked) && marked is true &&
                message.JsonRpcMessage is JsonRpcResponse response && response.Result?["tools"] is JsonArray listed)
                foreach (var item in listed.OfType<JsonObject>())
                {
                    if (item["name"]?.GetValue<string>() is not { } name || !OfficialTools.ToolNames.Contains(name))
                        continue;
                    if (OfficialContracts.Contract(name).TryGetProperty("execution", out var execution))
                        item["execution"] = JsonNode.Parse(execution.GetRawText());
                }

            await next(message, token).ConfigureAwait(false);
        });
    }

    private static void ValidateProfile(string profile)
    {
        if (profile is not ("agent" or "official" or "extended"))
            throw new ArgumentException("invalid_profile: Choose agent, official or extended.", nameof(profile));
    }
}
