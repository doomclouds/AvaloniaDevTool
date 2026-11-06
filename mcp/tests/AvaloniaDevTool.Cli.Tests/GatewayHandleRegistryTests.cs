using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaDevTool.Cli.Gateway;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class GatewayHandleRegistryTests
{
    [Fact]
    public void A_node_request_before_attachment_reports_the_missing_target()
    {
        var registry = new GatewayHandleRegistry();
        Assert.Contains("not_connected",
            Assert.Throws<McpException>(() =>
                registry.MapArguments(Request("ui-click", "nodeId", 1L))).Message);
    }

    [Fact]
    public void Attachment_error_preserves_nodes_when_scope_is_unchanged_and_retires_them_when_target_ended()
    {
        var registry = new GatewayHandleRegistry();
        var original = Body(registry.MapResult("search",
            Result(new
            {
                Matches = new[] { new { Id = 41L, TypeName = "Border", HasChildren = false } }
            })))["Matches"]![0]!["Id"]!.GetValue<long>();
        var unchanged = Result(new { Message = "Candidate failed before switching." });
        unchanged.IsError = true;
        registry.MapResult("attach-to-file", unchanged);
        Assert.Equal(41L, registry.MapArguments(Request("props", "nodeId", original)).Arguments!["nodeId"].GetInt64());

        var disconnected = Result(new { Message = "The old connection ended." }, target: null);
        disconnected.IsError = true;
        registry.MapResult("attach-to-app", disconnected);
        Assert.Contains("not_connected",
            Assert.Throws<McpException>(() =>
                registry.MapArguments(Request("props", "nodeId", original))).Message);
    }

    [Fact]
    public void Breakpoint_update_resolves_its_own_handle_and_nested_source_receiver_handles()
    {
        var registry = new GatewayHandleRegistry();
        var created = Body(registry.MapResult("breakpoint-create",
            Result(new
            {
                Id = 7L, Definition = new { ObjectId = 41L, SourceObjectId = 42L }
            })));
        var mapped = registry.MapArguments(new CallToolRequestParams
        {
            Name = "breakpoint-update", Arguments = new Dictionary<string, JsonElement>
            {
                ["id"] = JsonSerializer.SerializeToElement(created["Id"]!.GetValue<long>()),
                ["definition"] = JsonSerializer.SerializeToElement(new
                {
                    ObjectId = created["Definition"]!["ObjectId"]!.GetValue<long>(),
                    SourceObjectId = created["Definition"]!["SourceObjectId"]!.GetValue<long>()
                })
            }
        });
        Assert.Equal(7L, mapped.Arguments!["id"].GetInt64());
        Assert.Equal(41L, mapped.Arguments["definition"].GetProperty("ObjectId").GetInt64());
        Assert.Equal(42L, mapped.Arguments["definition"].GetProperty("SourceObjectId").GetInt64());
    }

    [Fact]
    public void Opaque_backend_nodes_roundtrip_and_content_reload_preserves_capture_ownership_scope()
    {
        var registry = new GatewayHandleRegistry();
        const long opaqueNode = 4_567_891_234_567;
        var tree = registry.MapResult("search",
            Result(new
            {
                Matches = new[] { new { Id = opaqueNode, TypeName = "Button", HasChildren = false } }, Visited = 1,
                Truncated = false
            }));
        var node = Body(tree)["Matches"]![0]!["Id"]!.GetValue<long>();
        Assert.NotEqual(opaqueNode, node);
        Assert.Equal(opaqueNode,
            registry.MapArguments(Request("ui-click", "nodeId", node)).Arguments!["nodeId"].GetInt64());
        var capture = registry.MapResult("capture-start", Result(new { CaptureId = "backend-capture" }));
        var captureId = Body(capture)["CaptureId"]!.GetValue<string>();

        registry.MapResult("tree", Result(new { Entries = Array.Empty<object>() }, content: 2));
        Assert.Contains("stale_node",
            Assert.Throws<McpException>(() => registry.MapArguments(Request("ui-click", "nodeId", node))).Message);
        Assert.Equal("backend-capture",
            registry.MapArguments(Request("capture-stop", "captureId", captureId)).Arguments!["captureId"].GetString());
        registry.MapResult("tree",
            Result(new { Entries = Array.Empty<object>() }, target: "second-target", content: 2));
        Assert.Contains("stale_capture",
            Assert.Throws<McpException>(() => registry.MapArguments(Request("capture-stop", "captureId", captureId)))
                .Message);
    }

    [Fact]
    public void Native_dialogs_share_native_identity_but_operations_have_distinct_kinds_and_machine_switch_retires_all()
    {
        var registry = new GatewayHandleRegistry();
        var windows = registry.MapResult("native-windows",
            Result(new[]
                {
                    new { NodeId = "native:opaque:window", Handle = 12345L, OwnerHandle = 0L, ProcessId = 99 }
                },
                native: "worker-one"));
        var node = Body(windows)[0]!["NodeId"]!.GetValue<string>();
        Assert.StartsWith("native:", node);
        Assert.Equal(12345L, Body(windows)[0]!["Handle"]!.GetValue<long>());
        Assert.Equal("native:opaque:window",
            registry.MapArguments(Request("file-dialog-inspect", "dialogId", node)).Arguments!["dialogId"].GetString());
        var action = registry.MapResult("native-action",
            Result(new
                {
                    NodeId = "native:opaque:window", OperationId = "opaque-operation", Status = "pending"
                },
                native: "worker-one"));
        var operation = Body(action)["OperationId"]!.GetValue<string>();
        Assert.Equal("opaque-operation",
            registry.MapArguments(Request("native-action-status", "operationId", operation)).Arguments!["operationId"]
                .GetString());
        Assert.Throws<McpException>(() => registry.MapArguments(Request("native-inspect", "nodeId", operation)));
        registry.RetireMachine();
        Assert.Throws<McpException>(() => registry.MapArguments(Request("native-inspect", "nodeId", node)));
        Assert.Throws<McpException>(() =>
            registry.MapArguments(Request("native-action-status", "operationId", operation)));
    }

    [Fact]
    public void Screenshot_metadata_is_remapped_and_encoded_image_payload_is_preserved()
    {
        var registry = new GatewayHandleRegistry();
        var result = Result(new { NodeId = 987654321L, Width = 2, Height = 3, CoordinateSpace = "control-local-dip" });
        var original = ImageContentBlock.FromBytes(new byte[] { 0, 1, 2, 255 }, "image/png");
        result.Content.Add(original);
        var mapped = registry.MapResult("ui-screenshot", result);
        var image = Assert.Single(mapped.Content.OfType<ImageContentBlock>());
        Assert.Equal(original.Data, image.Data);
        Assert.Equal(original.MimeType, image.MimeType);
        var metadata = Body(mapped);
        Assert.Equal(987654321L,
            registry.MapArguments(Request("props", "nodeId", metadata["NodeId"]!.GetValue<long>())).Arguments!["nodeId"]
                .GetInt64());
        Assert.Equal(2, metadata["Width"]!.GetValue<int>());
        Assert.Equal("control-local-dip", metadata["CoordinateSpace"]!.GetValue<string>());
    }

    [Fact]
    public void Capture_raw_records_and_unrecognized_property_payloads_are_never_treated_as_handles()
    {
        var registry = new GatewayHandleRegistry();
        var raw = new
        {
            NodeId = 12345, ObjectId = 54321, OperationId = "user-data-operation", Status = "pending",
            CaptureId = "user-data-capture", Uri = "file:///user-data"
        };
        var capture = registry.MapResult("capture-status",
            Result(new { CaptureId = "real-capture", Metrics = raw, Activity = raw }));
        var mapped = Body(capture);
        Assert.Equal(JsonSerializer.Serialize(raw), mapped["Metrics"]!.ToJsonString());
        Assert.Equal(JsonSerializer.Serialize(raw), mapped["Activity"]!.ToJsonString());
        Assert.NotEqual("real-capture", mapped["CaptureId"]!.GetValue<string>());
        var properties = registry.MapResult("props", Result(new[] { new { Id = "Text", Value = raw } }));
        Assert.Equal(JsonSerializer.Serialize(raw), Body(properties)[0]!["Value"]!.ToJsonString());
    }

    [Fact]
    public void Only_native_action_status_and_known_file_dialog_action_fields_can_keep_machine_busy()
    {
        var tracker = new NativeOperationTracker();
        var pending = new { OperationId = "real-native-operation", Status = "pending" };
        tracker.Observe("capture-status", Result(new { Metrics = pending }));
        tracker.Observe("props", Result(new[] { new { Value = pending } }));
        tracker.Observe("native-inspect", Result(new { Custom = pending }));
        Assert.False(tracker.HasPending);
        tracker.Observe("native-action", Result(pending));
        Assert.True(tracker.HasPending);
        tracker.Observe("native-action-status",
            Result(new { OperationId = "real-native-operation", Status = "completed" }));
        Assert.False(tracker.HasPending);
        tracker.Observe("file-dialog-inspect", Result(new { LastAction = pending }));
        Assert.True(tracker.HasPending);
        tracker.Observe("file-dialog-inspect",
            Result(new { LastAction = new { OperationId = "real-native-operation", Status = "failed" } }));
        Assert.False(tracker.HasPending);
    }

    private static CallToolRequestParams Request(string tool, string name, object value) => new()
    {
        Name = tool,
        Arguments = new Dictionary<string, JsonElement> { [name] = JsonSerializer.SerializeToElement(value) }
    };

    private static CallToolResult Result(object body,
        string? target = "first-target",
        long content = 1,
        string? native = null)
    {
        var node = JsonSerializer.SerializeToNode(new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(body) }]
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        node["_meta"] = new JsonObject
        {
            ["avdtExecutionScope"] = new JsonObject
            {
                ["session"] = target, ["contentGeneration"] = content, ["nativeSession"] = native
            }
        };
        return node.Deserialize<CallToolResult>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static JsonNode Body(CallToolResult result) => JsonNode.Parse(
        Assert.Single(result.Content.OfType<TextContentBlock>()).Text)!;
}
