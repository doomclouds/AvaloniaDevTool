using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli.Gateway;

/// <summary>Applies one managed gate and node identity contract to capability methods backed by raw target RPC.</summary>
internal sealed class ManagedIdentityTool(McpServerTool inner, ManagedToolContext context)
    : DelegatingMcpServerTool(inner)
{
    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default) => new(context.WithSessionAsync(cancellationToken,
        async (_, token) =>
        {
            // This call synchronizes the node session while the same managed gate remains held.
            var owner = context.OwnerSession;
            var client = await owner.GetAsync(token).ConfigureAwait(false);
            var identity = client.Session;
            await context.SynchronizeCurrentAsync(token).ConfigureAwait(false);
            var generation = client.Preview.CurrentContentGeneration;
            var parameters = GatewayArgumentHandles.Map(request.Params!,
                (kind, value) => kind == GatewayHandleKind.ManagedNode
                    ? JsonValue.Create(context.NativeNodeId(value.GetValue<long>()))!
                    : value.DeepClone());
            var result = await base.InvokeAsync(new RequestContext<CallToolRequestParams>(request.Server,
                    request.JsonRpcRequest,
                    parameters),
                token).ConfigureAwait(false);
            await context.SynchronizeCurrentAsync(token).ConfigureAwait(false);
            if (!ReferenceEquals(identity, client.Session) || generation != client.Preview.CurrentContentGeneration)
                throw new ModelContextProtocol.McpException("stale_session: The target changed during this operation.");
            return MapResult(parameters.Name, result);
        }));

    private CallToolResult MapResult(string name, CallToolResult result)
    {
        if (result.IsError == true)
            return result;
        var document = JsonSerializer.SerializeToNode(result, ExecutionScopeTool.Json)!.AsObject();
        if (document["content"] is JsonArray blocks)
            foreach (var block in blocks.OfType<JsonObject>())
                if (block["type"]?.GetValue<string>() == "text" && block["text"] is JsonValue text)
                {
                    JsonNode? body;
                    try
                    {
                        body = JsonNode.Parse(text.GetValue<string>());
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (body is null)
                        continue;
                    GatewayResultHandles.Map(name, body, Public);
                    block["text"] = body.ToJsonString();
                }

        if (document["structuredContent"] is { } structured)
            GatewayResultHandles.Map(name, GatewayResultHandles.StructuredBody(structured), Public);
        return document.Deserialize<CallToolResult>(ExecutionScopeTool.Json)!;

        JsonNode Public(GatewayHandleKind kind, JsonNode value) => kind == GatewayHandleKind.ManagedNode
            ? JsonValue.Create(context.PublicNodeId(value.GetValue<long>()))!
            : value.DeepClone();
    }
}
