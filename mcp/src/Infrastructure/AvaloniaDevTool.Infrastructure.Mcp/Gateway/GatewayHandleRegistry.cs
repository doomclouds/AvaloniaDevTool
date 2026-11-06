using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AvaloniaDevTool.Cli.Gateway;

internal enum GatewayHandleKind
{
    Application,
    ManagedNode,
    NativeNode,
    Operation,
    Capture,
    Asset,
    TreeCursor,
    MetricSeries,
    Breakpoint
}

/// <summary>Owns gateway references. Backend public IDs are opaque values, never bit-decoded.</summary>
internal sealed class GatewayHandleRegistry
{
    private static long _sequence =
        (long)(BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) & ((1UL << 52) - 1));

    private readonly string _instance = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<(GatewayHandleKind Kind, string Backend), JsonNode> _reverse = new();
    private readonly object _sync = new();
    private string? _target;
    private long _content;
    private string? _native;
    private string? _metricCapture;

    private sealed record Entry(GatewayHandleKind Kind, JsonNode Backend);

    internal void RetireMachine()
    {
        lock (_sync)
        {
            _entries.Clear();
            _reverse.Clear();
            _target = null;
            _native = null;
            _content = 0;
            _metricCapture = null;
        }
    }

    private void Retire(Func<GatewayHandleKind, bool> predicate)
    {
        foreach (var key in _entries.Where(pair => predicate(pair.Value.Kind)).Select(pair => pair.Key).ToArray())
            _entries.Remove(key);
        foreach (var key in _reverse.Keys.Where(key => predicate(key.Kind)).ToArray())
            _reverse.Remove(key);
    }

    private JsonNode Register(GatewayHandleKind kind, JsonNode value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<long>(out var number) && number == 0)
            return JsonValue.Create(0L)!;
        var key = (kind, value.ToJsonString());
        if (_reverse.TryGetValue(key, out var existing))
            return existing.DeepClone();
        if (_entries.Count >= 250_000)
            throw new McpException(
                "handle_limit: This gateway session retains at most 250000 handles. Reconnect to release them.");
        var id = Interlocked.Increment(ref _sequence);
        if (id > 9_007_199_254_740_991)
            throw new McpException("handle_limit: The gateway exhausted its safe numeric handle range.");
        JsonNode external = kind is GatewayHandleKind.ManagedNode or GatewayHandleKind.Application
            or GatewayHandleKind.MetricSeries or GatewayHandleKind.Breakpoint
            ? JsonValue.Create(id)!
            : JsonValue.Create(
                $"{(kind == GatewayHandleKind.NativeNode ? "native" : "avdt")}:gateway:{_instance}:{id}{(kind == GatewayHandleKind.Asset ? ":" + value.GetValue<string>() : "")}")
            !;
        _entries.Add(external.ToJsonString(), new(kind, value.DeepClone()));
        _reverse.Add(key, external.DeepClone());
        return external;
    }

    private JsonNode Resolve(GatewayHandleKind kind, JsonNode value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<long>(out var number) && number == 0)
            return JsonValue.Create(0L)!;
        if (_target is null && kind == GatewayHandleKind.ManagedNode)
            throw new McpException("not_connected: Attach to an application before accessing its nodes.");
        if (!_entries.TryGetValue(value.ToJsonString(), out var entry) || entry.Kind != kind)
            throw new McpException(kind switch
            {
                GatewayHandleKind.Application => "app_not_found: List applications again on the selected machine.",
                GatewayHandleKind.Capture => "stale_capture: Read captures from their original target session.",
                GatewayHandleKind.Asset => "invalid_asset_uri: List assets again on the selected target.",
                GatewayHandleKind.TreeCursor => "cursor_invalidated: Read the current branch again.",
                GatewayHandleKind.MetricSeries => "stale_series: Read metrics from the current capture again.",
                GatewayHandleKind.Breakpoint => "breakpoint_not_found: List breakpoints on the current target again.",
                GatewayHandleKind.Operation =>
                    "stale_operation: Inspect native operations again on the selected target.",
                _ => "stale_node: Read tree, search or native windows again on the selected target."
            });
        return entry.Backend.DeepClone();
    }

    internal CallToolRequestParams MapArguments(CallToolRequestParams parameters)
    {
        lock (_sync)
            return GatewayArgumentHandles.Map(parameters, Resolve);
    }

    internal CallToolResult MapResult(string tool, CallToolResult result) => MapResult(tool, result, out _);

    internal CallToolResult MapResult(string tool, CallToolResult result, out bool nativeScopeChanged)
    {
        nativeScopeChanged = false;
        lock (_sync)
        {
            var document = JsonSerializer.SerializeToNode(result, ExecutionScopeTool.Json)!.AsObject();
            if (document["_meta"]?["avdtExecutionScope"] is not JsonObject scope)
            {
                if (result.IsError == true)
                    return result;
                throw new McpException("incompatible_executor: The endpoint did not return execution scope metadata.");
            }

            var target = scope["session"]?.GetValue<string>();
            var content = scope["contentGeneration"]?.GetValue<long>() ?? 0;
            var native = scope["nativeSession"]?.GetValue<string>();
            nativeScopeChanged = target != _target || native != _native;
            if (target != _target)
                Retire(kind => kind != GatewayHandleKind.Application);
            else
            {
                if (content != _content)
                    Retire(kind => kind is GatewayHandleKind.ManagedNode or GatewayHandleKind.Asset
                        or GatewayHandleKind.TreeCursor or GatewayHandleKind.Breakpoint);
                if (native != _native)
                    Retire(kind => kind is GatewayHandleKind.NativeNode or GatewayHandleKind.Operation);
            }

            _target = target;
            _content = content;
            _native = native;

            if (result.IsError == true)
                return result;

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
                        TrackMetricScope(tool, body);
                        GatewayResultHandles.Map(tool, body, Register);
                        block["text"] = body.ToJsonString();
                    }
                    else if (tool == "open-asset" && block["type"]?.GetValue<string>() == "resource" &&
                             block["resource"] is JsonObject resource && resource["uri"] is { } uri)
                        resource["uri"] = Register(GatewayHandleKind.Asset, uri);

            if (document["structuredContent"] is { } structured)
            {
                TrackMetricScope(tool, GatewayResultHandles.StructuredBody(structured));
                GatewayResultHandles.Map(tool, GatewayResultHandles.StructuredBody(structured), Register);
            }

            // Image/resource blocks remain encoded payloads. No image decoding or re-encoding occurs here.
            return document.Deserialize<CallToolResult>(ExecutionScopeTool.Json)!;
        }
    }

    private void TrackMetricScope(string tool, JsonNode body)
    {
        JsonNode? status = tool == "metrics"
            ? GatewayResultHandles.Get(body, "Status")
            : tool.StartsWith("metrics-", StringComparison.Ordinal)
                ? body
                : tool.StartsWith("capture-", StringComparison.Ordinal)
                    ? GatewayResultHandles.Get(GatewayResultHandles.Get(body, "Metrics"), "Status")
                    : null;
        if (status is null)
            return;
        var capture = GatewayResultHandles.Get(status, "CaptureId")?.GetValue<string>();
        if (capture != _metricCapture)
        {
            Retire(kind => kind == GatewayHandleKind.MetricSeries);
            _metricCapture = capture;
        }
    }
}
