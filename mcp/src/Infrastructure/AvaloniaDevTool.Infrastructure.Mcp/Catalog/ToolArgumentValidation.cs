using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AvaloniaDevTool.Cli.Catalog;

/// <summary>Validates schemas generated from current capability signatures, without historical schema overrides.</summary>
internal static class ToolArgumentValidation
{
    internal static void Validate(CallToolRequestParams request, JsonElement schema)
    {
        var arguments = JsonSerializer.SerializeToElement(request.Arguments ?? new Dictionary<string, JsonElement>());
        if (!Matches(arguments, schema, schema, 0))
            throw new McpException("invalid_argument: Arguments do not match this tool's current schema.");
    }

    private static bool Matches(JsonElement value, JsonElement rule, JsonElement root, int depth)
    {
        if (depth > 48)
            return false;
        if (rule.TryGetProperty("$ref", out var reference))
        {
            var resolved = root;
            foreach (var segment in reference.GetString()![2..].Split('/'))
                if (!resolved.TryGetProperty(segment.Replace("~1", "/").Replace("~0", "~"), out resolved))
                    return false;
            return Matches(value, resolved, root, depth + 1);
        }

        if (rule.TryGetProperty("anyOf", out var alternatives) &&
            !alternatives.EnumerateArray().Any(option => Matches(value, option, root, depth + 1)))
            return false;
        if (rule.TryGetProperty("enum", out var choices) &&
            !choices.EnumerateArray().Any(option => JsonElement.DeepEquals(option, value)))
            return false;
        if (rule.TryGetProperty("type", out var type))
        {
            IEnumerable<string?> types = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Select(item => item.GetString())
                : [type.GetString()];
            if (!types.Any(name => name switch
                {
                    "null" => value.ValueKind == JsonValueKind.Null,
                    "object" => value.ValueKind == JsonValueKind.Object,
                    "array" => value.ValueKind == JsonValueKind.Array,
                    "string" => value.ValueKind == JsonValueKind.String,
                    "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                    "number" => value.ValueKind == JsonValueKind.Number,
                    "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    _ => true
                }))
                return false;
        }

        if (value.ValueKind == JsonValueKind.Array && rule.TryGetProperty("items", out var items) &&
            value.EnumerateArray().Any(item => !Matches(item, items, root, depth + 1)))
            return false;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (rule.TryGetProperty("required", out var required) &&
                required.EnumerateArray().Any(item => !value.TryGetProperty(item.GetString()!, out _)))
                return false;
            if (rule.TryGetProperty("properties", out var fields))
                foreach (var field in value.EnumerateObject())
                    if (fields.TryGetProperty(field.Name, out var member))
                    {
                        if (!Matches(field.Value, member, root, depth + 1))
                            return false;
                    }
                    else if (rule.TryGetProperty("additionalProperties", out var additional) &&
                             additional.ValueKind == JsonValueKind.False)
                        return false;
        }

        return true;
    }
}
