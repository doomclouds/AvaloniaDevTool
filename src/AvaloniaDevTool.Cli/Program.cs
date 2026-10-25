using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace AvaloniaDevTool.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("AvaloniaDevTool.Cli mcp [--pipe NAME] [--profile agent|official|extended]");
            Console.WriteLine("Starts the independent stdio MCP server. Use attach-to-app to choose a local target.");
            return 0;
        }
        string? initialPipe = null;
        var profile = "agent";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (args[0] != "mcp")
        {
            Console.Error.WriteLine("Usage: AvaloniaDevTool.Cli mcp [--pipe NAME] [--profile agent|official|extended]");
            return 2;
        }
        for (var i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !seen.Add(args[i]) || string.IsNullOrWhiteSpace(args[i + 1]) ||
                args[i] is not ("--pipe" or "--profile"))
            {
                Console.Error.WriteLine("invalid_arguments: Each option requires one nonempty value and may appear only once.");
                return 2;
            }
            if (args[i] == "--pipe") initialPipe = args[i + 1];
            else profile = args[i + 1];
        }
        if (profile is not ("agent" or "official" or "extended"))
        {
            Console.Error.WriteLine("invalid_profile: Choose agent, official or extended.");
            return 2;
        }
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        await using var session = new ToolSession(initialPipe);
        builder.Services.AddSingleton(session);
        var server = builder.Services.AddMcpServer().WithStdioServerTransport();
        if (profile is "official" or "agent")
        {
            var target = new OfficialTools(session);
            var tools = typeof(OfficialTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Select(method => OfficialContracts.Declare(McpServerTool.Create(method, target)));
            server.WithTools(tools);
            var agentSchemas = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (profile == "agent")
            {
                var agent = new AgentTools(session, target);
                var additions = typeof(AgentTools).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                    .Select(method => McpServerTool.Create(method, agent)).ToArray();
                foreach (var tool in additions)
                    agentSchemas[tool.ProtocolTool.Name] = JsonSerializer.SerializeToElement(tool.ProtocolTool,
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("inputSchema");
                server.WithTools(additions);
            }
            server.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (request, token) =>
            {
                OfficialContracts.Validate(request.Params!);
                if (agentSchemas.TryGetValue(request.Params!.Name, out var schema))
                    OfficialContracts.ValidateSchema(request.Params, schema);
                return await next(request, token).ConfigureAwait(false);
            }));
            const string listMarker = "AvaloniaDevTool.OfficialToolsList";
            server.WithMessageFilters(filters =>
            {
                filters.AddIncomingFilter(next => async (context, token) =>
                {
                    if (context.JsonRpcMessage is JsonRpcRequest { Method: RequestMethods.ToolsList })
                        context.Items[listMarker] = true;
                    await next(context, token).ConfigureAwait(false);
                });
                filters.AddOutgoingFilter(next => async (context, token) =>
                {
                    if (context.Items.TryGetValue(listMarker, out var marked) && marked is true &&
                        context.JsonRpcMessage is JsonRpcResponse response && response.Result?["tools"] is JsonArray listed)
                        foreach (var item in listed.OfType<JsonObject>())
                        {
                            if (item["name"]?.GetValue<string>() is not { } name || !OfficialTools.ToolNames.Contains(name)) continue;
                            if (OfficialContracts.Contract(name).TryGetProperty("execution", out var execution))
                                item["execution"] = JsonNode.Parse(execution.GetRawText());
                        }
                    await next(context, token).ConfigureAwait(false);
                });
            });
        }
        else server.WithTools<InspectorTools>(new InspectorTools(session));
        await builder.Build().RunAsync();
        return 0;
    }
}

