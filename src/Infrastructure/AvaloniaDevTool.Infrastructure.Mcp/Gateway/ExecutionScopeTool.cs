using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli.Gateway;

/// <summary>Publishes opaque scope facts for a gateway; does not change tool schemas or application results.</summary>
internal sealed class ExecutionScopeTool(McpServerTool inner, ToolSession session) : DelegatingMcpServerTool(inner)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        CallToolResult result;
        try
        {
            result = await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (McpException error)
        {
            // An unsuccessful attachment can still change the connection. Include its actual scope as well.
            result = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error.Message }] };
        }

        var document = JsonSerializer.SerializeToNode(result, Json)!.AsObject();
        var metadata = document["_meta"] as JsonObject;
        if (metadata is null)
        {
            metadata = new JsonObject();
            document["_meta"] = metadata;
        }

        metadata["avdtExecutionScope"] = JsonSerializer.SerializeToNode(new
            {
                Session = session.CurrentSession?.SessionId,
                ContentGeneration = session.CurrentContentGeneration,
                NativeSession = session.NativeSessionIdentity
            },
            Json);
        return document.Deserialize<CallToolResult>(Json)!;
    }
}
