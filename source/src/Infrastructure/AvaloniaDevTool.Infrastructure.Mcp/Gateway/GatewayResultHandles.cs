using System.Text.Json.Nodes;

namespace AvaloniaDevTool.Cli.Gateway;

/// <summary>Explicit result contracts. Never walks arbitrary diagnostic records or user property values.</summary>
internal static class GatewayResultHandles
{
    internal static JsonNode StructuredBody(JsonNode node) => node is JsonObject { Count: 1 } obj &&
                                                              Get(obj, "result") is { } wrapped
        ? wrapped
        : node;

    internal static void Map(string tool, JsonNode body, Func<GatewayHandleKind, JsonNode, JsonNode> register)
    {
        switch (tool)
        {
            case "attach-to-app":
                Each(Get(body, "Applications"), item => Field(item, "Id", GatewayHandleKind.Application));
                break;
            case "tree":
                Each(Get(body, "Entries"),
                    item =>
                    {
                        Node(Get(item, "Node"));
                        Field(item, "ParentId");
                    });
                break;
            case "ancestors":
                Each(body, Node);
                break;
            case "search":
                Each(Get(body, "Matches"), Node);
                break;
            case "tree-children":
                Each(Get(body, "Children"), Node);
                Field(body, "NextCursor", GatewayHandleKind.TreeCursor);
                break;
            case "template-parent-selector":
                Field(body, "ObjectId");
                Field(body, "TemplateParentId");
                break;
            case "element-metadata":
                Field(body, "ObjectId");
                break;
            case "object":
                Field(body, "ObjectId");
                Each(Get(body, "Members"), member => Field(Get(member, "Value"), "ValueObjectId"));
                Each(Get(body, "Items"),
                    item =>
                    {
                        Field(Get(item, "Key"), "ValueObjectId");
                        Field(Get(item, "Value"), "ValueObjectId");
                    });
                break;
            case "props":
                Each(body, item => Field(Get(item, "Presentation"), "ValueId"));
                break;
            case "styles":
                Each(Get(body, "Bindings"), item => Binding(Get(item, "Details")));
                break;
            case "resources":
                Field(body, "NodeId");
                Each(Get(Get(body, "Tree"), "Roots"), Resource);
                Each(Get(body, "Entries"), ResourceEntry);
                break;
            case "resource-tree":
                Each(Get(body, "Roots"), Resource);
                break;
            case "resource-query":
                Field(body, "DictionaryId");
                Each(Get(body, "Entries"), ResourceEntry);
                break;
            case "resource-entry":
                ResourceEntry(body);
                break;
            case "assets":
                Each(body, item => Field(item, "Uri", GatewayHandleKind.Asset));
                break;
            case "open-asset":
            case "export-asset":
                Field(body, "Uri", GatewayHandleKind.Asset);
                break;
            case "ui-wait":
            case "ui-screenshot":
                Field(body, "NodeId");
                break;
            case "native-windows":
                Each(body, NativeNode);
                break;
            case "native-tree":
                Each(Get(body, "Nodes"), NativeNode);
                break;
            case "native-find":
                Each(Get(body, "Matches"), NativeNode);
                break;
            case "native-inspect":
                NativeNode(body);
                break;
            case "native-action":
            case "native-action-status":
                NativeAction(body);
                break;
            case "native-input":
            case "native-screenshot":
                NativeNode(body);
                break;
            case "renderer-diagnostics":
            case "renderer-diagnostics-set":
            case "renderer-diagnostics-stop":
                Each(Get(body, "Windows"), item => Field(item, "WindowId"));
                break;
            case "layout":
                Field(body, "RootObjectId");
                Each(Get(body, "Layers"),
                    item =>
                    {
                        Field(item, "ObjectId");
                        Field(item, "ParentId");
                    });
                break;
            case "composition":
                Field(body, "RootObjectId");
                Each(Get(body, "Layers"), item => Field(item, "ObjectId"));
                break;
            case "overlay":
            case "overlay-set":
                Field(body, "ObjectId");
                Field(body, "RootObjectId");
                break;
            case "popup-freeze":
            case "popup-freeze-set":
            case "popup-freeze-resume":
                Each(Get(body, "Roots"), item => Field(item, "RootId"));
                Each(Get(body, "Popups"),
                    item =>
                    {
                        Field(item, "PopupId");
                        Field(item, "RootId");
                    });
                break;
            case "metrics":
                Each(Get(body, "Series"), item => Field(item, "Id", GatewayHandleKind.MetricSeries));
                Each(Get(body, "Samples"), item => Field(item, "SeriesId", GatewayHandleKind.MetricSeries));
                Each(Get(body, "Frames"), item => Field(item, "SeriesId", GatewayHandleKind.MetricSeries));
                break;
            case "activity-start":
            case "activity-stop":
            case "events-start":
                Field(body, "ObjectId");
                break;
            case "activity":
                Each(body,
                    item =>
                    {
                        Field(item, "ObjectId");
                        Field(item, "HandlerObjectId");
                    });
                break;
            case "events-routes":
                Each(body,
                    route =>
                    {
                        Field(route, "SourceObjectId");
                        Each(Get(route, "Nodes"), item => Field(item, "ReceiverObjectId"));
                    });
                break;
            case "breakpoints":
                Each(body, Breakpoint);
                Each(Get(body, "Hits"),
                    hit =>
                    {
                        Field(hit, "BreakpointId", GatewayHandleKind.Breakpoint);
                        Field(hit, "ObjectId");
                    });
                break;
            case "breakpoint-create":
            case "breakpoint-update":
                Breakpoint(body);
                break;
            case "file-dialogs":
                Each(body, Dialog);
                break;
            case "file-dialog-inspect":
            case "file-dialog-set-paths":
            case "file-dialog-set-filter":
            case "file-dialog-accept":
            case "file-dialog-cancel":
            case "file-dialog-overwrite":
            case "file-dialog-wait":
                Dialog(body);
                break;
            // Nested source capture IDs, samples, logs, tags and event payloads are raw observations.
            case "capture-start":
            case "capture-status":
            case "capture-stop":
                Field(body, "CaptureId", GatewayHandleKind.Capture);
                break;
        }

        return;

        void Field(JsonNode? node, string name, GatewayHandleKind kind = GatewayHandleKind.ManagedNode)
        {
            if (node is not JsonObject obj)
                return;
            var entry = obj.FirstOrDefault(field => field.Key.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry.Value is JsonValue value)
                obj[entry.Key] = register(kind, value);
        }

        void Node(JsonNode? node)
        {
            Field(node, "Id");
            Field(node, "TemplateOwnerId");
            Field(node, "SlotContentId");
        }

        void NativeNode(JsonNode? node)
        {
            Field(node, "NodeId", GatewayHandleKind.NativeNode);
            Field(node, "ParentNodeId", GatewayHandleKind.NativeNode);
        }

        void NativeAction(JsonNode? node)
        {
            NativeNode(node);
            Field(node, "OperationId", GatewayHandleKind.Operation);
        }

        void Resource(JsonNode? node)
        {
            Field(node, "Id");
            Each(Get(node, "Children"), Resource);
        }

        void ResourceEntry(JsonNode? node)
        {
            Field(node, "OwnerDictionaryId");
            Field(node, "KeyObjectId");
            Field(node, "ValueObjectId");
        }

        void Breakpoint(JsonNode? node)
        {
            Field(node, "Id", GatewayHandleKind.Breakpoint);
            Field(Get(node, "Definition"), "ObjectId");
            Field(Get(node, "Definition"), "SourceObjectId");
        }

        void Binding(JsonNode? node)
        {
            foreach (var field in BindingFields)
                Field(Get(node, field), "ObjectId");
            Each(Get(node, "Children"), Binding);
        }

        void Dialog(JsonNode? node)
        {
            Field(node, "DialogId", GatewayHandleKind.NativeNode);
            NativeNode(Get(node, "Window"));
            foreach (var field in DialogNodeFields)
                Field(node, field, GatewayHandleKind.NativeNode);
            NativeAction(Get(node, "LastAction"));
            Each(Get(node, "Prompts"),
                prompt =>
                {
                    NativeNode(Get(prompt, "Window"));
                    Field(prompt, "YesNodeId", GatewayHandleKind.NativeNode);
                    Field(prompt, "NoNodeId", GatewayHandleKind.NativeNode);
                });
        }
    }

    internal static JsonNode? Get(JsonNode? node, string name) => node is JsonObject obj
        ? obj.FirstOrDefault(field => field.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value
        : null;

    private static void Each(JsonNode? node, Action<JsonNode?> action)
    {
        if (node is JsonArray array)
            foreach (var item in array)
                action(item);
    }

    private static readonly string[] DialogNodeFields =
        ["PathInputNodeId", "AcceptNodeId", "CancelNodeId", "FilterNodeId"];

    private static readonly string[] BindingFields =
    [
        "SourceType", "Source", "Path", "Mode", "Converter", "ConverterParameter", "FallbackValue",
        "TargetNullValue", "UpdateSourceTrigger", "StringFormat", "ElementName", "RelativeSource",
        "DataContext", "DataContextOrigin", "CurrentValue", "ResourceKey", "ConverterCulture", "ConverterCultureOrigin"
    ];
}
