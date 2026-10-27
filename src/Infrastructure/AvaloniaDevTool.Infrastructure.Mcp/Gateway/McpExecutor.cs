using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaDevTool.Cli.Catalog;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli.Gateway;

// This interface belongs to the MCP adapter. Domain and Application never receive tool names or MCP results.
internal interface IMcpExecutor : IAsyncDisposable
{
    string Machine { get; }

    Task<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CallToolRequestParams parameters,
        CancellationToken token);
}

internal sealed class LocalExecutor : IMcpExecutor
{
    private readonly ToolSession _session;
    private readonly IReadOnlyDictionary<string, McpServerTool> _tools;
    public string Machine => "local";
    internal IReadOnlyList<McpServerTool> Tools { get; }

    public LocalExecutor(string? pipe)
    {
        _session = new ToolSession(pipe);
        Tools = McpToolCatalog.CreateTools(_session);
        _tools = Tools.ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
    }

    public async Task<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CallToolRequestParams parameters,
        CancellationToken token)
    {
        if (!_tools.TryGetValue(parameters.Name, out var tool))
            throw new McpException("unknown_tool: The local executor does not support this tool.");
        var context = new RequestContext<CallToolRequestParams>(request.Server, request.JsonRpcRequest, parameters);
        return await tool.InvokeAsync(context, token).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();
}

internal sealed class HttpExecutor : IMcpExecutor
{
    private readonly McpClient _client;
    public string Machine { get; }

    private HttpExecutor(McpClient client, Uri endpoint)
    {
        _client = client;
        Machine = endpoint.AbsoluteUri;
    }

    public static async Task<HttpExecutor> ConnectAsync(Uri endpoint,
        IReadOnlyList<McpServerTool> expected,
        CancellationToken token)
    {
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp,
                EnableStandaloneGetStream = false, OwnsSession = true, Name = "AvaloniaDevTool execution endpoint"
            },
            http,
            loggerFactory: null,
            ownsHttpClient: true);
        McpClient? client = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            client = await McpClient.CreateAsync(transport,
                new McpClientOptions { ProtocolVersion = ExecutionProtocol.Version },
                cancellationToken: timeout.Token).ConfigureAwait(false);
            var actual = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            if (actual.Any(tool => tool.Name == "select-machine"))
                throw new McpException("incompatible_executor: Select a serve endpoint, not another gateway.");
            var catalog = actual.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
            foreach (var tool in expected)
                if (!catalog.TryGetValue(tool.ProtocolTool.Name, out var remote) ||
                    !JsonNode.DeepEquals(JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()),
                        JsonNode.Parse(remote.ProtocolTool.InputSchema.GetRawText())))
                    throw new McpException(
                        $"incompatible_executor: Missing or incompatible tool {tool.ProtocolTool.Name}.");
            // This is discovery only (id omitted), so preparation neither attaches nor claims desktop control.
            // Check scope support before a caller can start any listener or issue another mutation.
            var discovery = await client
                .CallToolAsync(new CallToolRequestParams { Name = "attach-to-app" }, timeout.Token)
                .ConfigureAwait(false);
            var discoveryJson = JsonSerializer.SerializeToNode(discovery, ExecutionScopeTool.Json);
            if (discovery.IsError == true || discoveryJson?["_meta"]?["avdtExecutionScope"] is not JsonObject)
                throw new McpException(
                    "incompatible_executor: The endpoint does not support scoped execution metadata.");
            return new HttpExecutor(client, endpoint);
        }
        catch
        {
            if (client is not null)
                await client.DisposeAsync().ConfigureAwait(false);
            else
                await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CallToolRequestParams parameters,
        CancellationToken token)
    {
        try
        {
            // Exactly one request. In particular, never reconnect and replay an uncertain mutation.
            // Protocol-version, progress and request-state metadata belong to this hop, not the external stdio hop.
            return await _client.CallToolAsync(new CallToolRequestParams
                    { Name = parameters.Name, Arguments = parameters.Arguments },
                token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or McpException
                                          or InvalidOperationException or TimeoutException)
        {
            throw new ExecutorDisconnectedException(error);
        }
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}

internal sealed class ExecutorDisconnectedException(Exception inner)
    : IOException("The selected executor disconnected. The operation may have executed; it was not retried.", inner);
