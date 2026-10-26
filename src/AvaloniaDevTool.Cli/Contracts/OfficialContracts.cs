using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

internal static class OfficialContracts
{
    private static readonly JsonDocument Contracts = Load();

    private static JsonDocument Load()
    {
        var assembly = typeof(OfficialContracts).Assembly;
        var name = assembly.GetManifestResourceNames().Single(value =>
            value.EndsWith("official-input-contracts.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return JsonDocument.Parse(stream);
    }

    internal static JsonElement Contract(string name) => Contracts.RootElement.GetProperty(name);

    internal static void Validate(CallToolRequestParams request)
    {
        if (!Contracts.RootElement.TryGetProperty(request.Name, out var contract))
            return;
        ValidateSchema(request, contract.GetProperty("inputSchema"));
    }

    internal static void ValidateSchema(CallToolRequestParams request, JsonElement schema)
    {
        var arguments = request.Arguments;
        if (schema.TryGetProperty("required", out var required))
            foreach (var field in required.EnumerateArray())
                if (arguments is null || !arguments.ContainsKey(field.GetString()!))
                    throw new McpException($"invalid_argument: Missing required parameter '{field.GetString()}'.");
        if (arguments is null || !schema.TryGetProperty("properties", out var properties))
            return;
        foreach (var argument in arguments)
            if (properties.TryGetProperty(argument.Key, out var rule) && !Matches(argument.Value, rule, schema, 0))
                throw new McpException(
                    $"invalid_argument: Parameter '{argument.Key}' does not match its declared type or allowed values.");
    }

    private static bool Matches(JsonElement value, JsonElement rule, JsonElement root, int depth)
    {
        if (depth >= 32)
            return false;
        if (rule.TryGetProperty("$ref", out var reference))
        {
            var path = reference.GetString();
            if (path is null || !path.StartsWith("#/", StringComparison.Ordinal))
                return false;
            var resolved = root;
            foreach (var part in path[2..].Split('/'))
                if (!resolved.TryGetProperty(part.Replace("~1", "/").Replace("~0", "~"), out resolved))
                    return false;
            return Matches(value, resolved, root, depth + 1);
        }

        if (rule.TryGetProperty("anyOf", out var any))
            return any.EnumerateArray().Any(item => Matches(value, item, root, depth + 1));
        if (rule.TryGetProperty("oneOf", out var one))
            return one.EnumerateArray().Count(item => Matches(value, item, root, depth + 1)) == 1;
        if (rule.TryGetProperty("type", out var type))
        {
            var allowed = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(item => item.GetString())
                : [type.GetString()];
            if (!allowed.Any(candidate => candidate switch
                {
                    "null" => value.ValueKind == JsonValueKind.Null,
                    "string" => value.ValueKind == JsonValueKind.String,
                    "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                    "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var numeric) &&
                                double.IsFinite(numeric),
                    "array" => value.ValueKind == JsonValueKind.Array,
                    "object" => value.ValueKind == JsonValueKind.Object,
                    _ => false
                }))
                return false;
        }

        if (rule.TryGetProperty("enum", out var values) &&
            !values.EnumerateArray().Any(item => JsonElement.DeepEquals(value, item)))
            return false;
        if (value.ValueKind == JsonValueKind.Array && rule.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray())
                if (!Matches(item, items, root, depth + 1))
                    return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (rule.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray())
                    if (!value.TryGetProperty(name.GetString()!, out _))
                        return false;
            if (rule.TryGetProperty("properties", out var properties))
                foreach (var field in value.EnumerateObject())
                    if (properties.TryGetProperty(field.Name, out var member) &&
                        !Matches(field.Value, member, root, depth + 1))
                        return false;
        }

        return true;
    }

    internal static McpServerTool Declare(McpServerTool tool) => new DeclaredTool(tool);

    private sealed class DeclaredTool : DelegatingMcpServerTool
    {
        private readonly Tool _metadata;

        public DeclaredTool(McpServerTool inner) : base(inner)
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var node = JsonSerializer.SerializeToNode(inner.ProtocolTool, options)!.AsObject();
            var contract = Contract(inner.ProtocolTool.Name);
            node["inputSchema"] = JsonNode.Parse(contract.GetProperty("inputSchema").GetRawText());
            node.Remove("outputSchema");
            if (contract.TryGetProperty("annotations", out var annotations))
                node["annotations"] = JsonNode.Parse(annotations.GetRawText());
            // The SDK Tool model omits the captured execution extension; message filters preserve it on the wire.
            _metadata = node.Deserialize<Tool>(options)!;
        }

        public override Tool ProtocolTool => _metadata;
    }
}
