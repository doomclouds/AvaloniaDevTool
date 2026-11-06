using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace AvaloniaDevTool.Cli.Gateway;

internal static class GatewayArgumentHandles
{
    internal static CallToolRequestParams Map(CallToolRequestParams request,
        Func<GatewayHandleKind, JsonNode, JsonNode> resolve)
    {
        var json = JsonSerializer.SerializeToNode(request, ExecutionScopeTool.Json)!.AsObject();
        if (json["arguments"] is not JsonObject args)
            return request;
        var native = request.Name.StartsWith("native-", StringComparison.Ordinal) ||
                     request.Name.StartsWith("file-dialog", StringComparison.Ordinal);
        foreach (var field in args.ToArray())
        {
            if (field.Value is null)
                continue;
            GatewayHandleKind? kind = field.Key switch
            {
                "id" when request.Name == "attach-to-app" => GatewayHandleKind.Application,
                "id" when request.Name is "breakpoint-update" or "breakpoint-remove" => GatewayHandleKind.Breakpoint,
                "nodeId" or "rootNodeId" or "targetNodeId" => native
                    ? GatewayHandleKind.NativeNode
                    : GatewayHandleKind.ManagedNode,
                "objectId" or "sourceObjectId" or "sourceNodeId" or "scopeNodeId" or "dictionaryId" or "keyObjectId"
                    or "windowId" or "rootId" => GatewayHandleKind.ManagedNode,
                "dialogId" or "promptId" => GatewayHandleKind.NativeNode,
                "operationId" => GatewayHandleKind.Operation,
                "captureId" => GatewayHandleKind.Capture,
                "assetUrl" => GatewayHandleKind.Asset,
                "uri" when request.Name == "export-asset" => GatewayHandleKind.Asset,
                "cursor" when request.Name == "tree-children" => GatewayHandleKind.TreeCursor,
                "seriesId" when request.Name == "metrics-clear" => GatewayHandleKind.MetricSeries,
                _ => null
            };
            if (kind is { } selected)
                args[field.Key] = resolve(selected, field.Value);
            else if (field.Key == "path" && request.Name == "ui-drag" && field.Value is JsonArray points)
            {
                foreach (var point in points.OfType<JsonObject>())
                    MapField(point, "nodeId");
            }
            else if (field.Key == "nodeIds" && request.Name == "tree-release" && field.Value is JsonArray nodes)
            {
                for (var index = 0; index < nodes.Count; index++)
                    if (nodes[index] is { } node)
                        nodes[index] = resolve(GatewayHandleKind.ManagedNode, node);
            }
            else if (field.Key == "definition" && request.Name == "breakpoint-update" &&
                     field.Value is JsonObject definition)
            {
                MapField(definition, "ObjectId");
                MapField(definition, "SourceObjectId");
            }
        }

        return json.Deserialize<CallToolRequestParams>(ExecutionScopeTool.Json)!;

        void MapField(JsonObject obj, string name)
        {
            var field = obj.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (field.Value is not null)
                obj[field.Key] = resolve(GatewayHandleKind.ManagedNode, field.Value);
        }
    }
}
