using System.Globalization;
using System.Text;
using System.Text.Json;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class LogSnapshots
{
    internal const int MaximumParameters = 32;
    private const int MaximumTemplate = 4096;
    private const int MaximumValue = 1024;
    private const int MaximumExceptions = 8;
    internal const int MaximumTextBytes = 96 * 1024;

    public static LogDetails Capture(DiagnosticsLogEvent entry, out string message)
    {
        var budget = new TextBudget(MaximumTextBytes);
        var template = entry.MessageTemplate is null
            ? null
            : budget.Take(entry.MessageTemplate, MaximumTemplate, out _);
        message = budget.Take(FormatMessage(entry.MessageTemplate, entry.Parameters), MaximumTemplate, out _);
        var parameters = CaptureParameters(entry.Parameters, budget, out var truncated);
        var remaining = MaximumExceptions;
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var exception = entry.Exception is null
            ? null
            : CaptureException(entry.Exception, 0, seen, budget, ref remaining);
        var sourceType = entry.Source is null ? null : budget.Take(entry.Source.GetType().FullName, 512, out _);
        var origin = budget.Take(entry.Origin, 128, out _);
        var eventName = entry.EventName is null ? null : budget.Take(entry.EventName, 256, out _);
        var scopes = new List<LogScopeInfo>();
        if (entry.Scopes is { } sourceScopes)
            for (var index = 0; index < Math.Min(sourceScopes.Count, 8); index++)
            {
                var values = CaptureParameters(sourceScopes[index], budget, out var scopeTruncated);
                scopes.Add(new LogScopeInfo(values, scopeTruncated));
            }

        return new LogDetails(template,
            parameters,
            entry.Parameters is null ? LogValueState.Unavailable : LogValueState.Known,
            exception,
            sourceType,
            origin,
            entry.EventId,
            eventName,
            scopes,
            truncated,
            entry.MessageTemplate?.Length > MaximumTemplate,
            budget.IsTruncated);
    }

    public static IReadOnlyList<KeyValuePair<string, object?>> AvaloniaParameters(string? template, object?[]? values)
    {
        if (values is null || values.Length == 0)
            return [];
        var names = template is null ? [] : ParameterNames(template);
        return values.Take(MaximumParameters + 1).Select((value, index) =>
                new KeyValuePair<string, object?>(index < names.Count ? names[index] : $"Argument{index}", value))
            .ToArray();
    }

    private static List<string> ParameterNames(string template)
    {
        var result = new List<string>();
        for (var index = 0;
             index < Math.Min(template.Length, MaximumTemplate) && result.Count < MaximumParameters + 1;
             index++)
        {
            if (template[index] != '{')
                continue;
            if (index + 1 < template.Length && template[index + 1] == '{')
            {
                index++;
                continue;
            }

            var end = template.IndexOf('}', index + 1, Math.Min(template.Length, MaximumTemplate) - index - 1);
            if (end < 0)
                break;
            var token = template[(index + 1)..end];
            var separator = token.IndexOfAny([':', ',']);
            result.Add(Limit(separator < 0 ? token : token[..separator], 128));
            index = end;
        }

        return result;
    }

    private static IReadOnlyList<LogParameterInfo> CaptureParameters(
        IReadOnlyList<KeyValuePair<string, object?>>? values,
        TextBudget budget,
        out bool truncated)
    {
        truncated = values?.Count > MaximumParameters;
        if (values is null)
            return [];
        var result = new List<LogParameterInfo>();
        for (var index = 0; index < Math.Min(values.Count, MaximumParameters); index++)
        {
            var (name, value) = values[index];
            var known = IsScalar(value);
            var parameterName = budget.Take(name, 128, out var nameTruncated);
            var typeName = budget.Take(value?.GetType().FullName ?? "null", 512, out var typeTruncated);
            var valueTruncated = false;
            var formatted = value is null || !known
                ? null
                : budget.Take(SafeFormat(value, null), MaximumValue, out valueTruncated);
            result.Add(new LogParameterInfo(parameterName,
                typeName,
                formatted,
                value is null ? LogValueState.Null : known ? LogValueState.Known : LogValueState.Unavailable,
                nameTruncated || typeTruncated || valueTruncated));
            truncated |= nameTruncated || typeTruncated || valueTruncated;
        }

        return result;
    }

    private static bool IsScalar(object? value) => value is null or string or char or bool or byte or sbyte or short
        or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset
        or DateOnly or TimeOnly or TimeSpan or Guid or Enum;

    private static string SafeFormat(object? value, string? format)
    {
        if (value is null)
            return "(null)";
        if (!IsScalar(value))
            return $"[{Limit(value.GetType().Name, 128)}: unavailable]";
        try
        {
            return Limit(value is IFormattable formattable
                    ? formattable.ToString(format, CultureInfo.InvariantCulture)
                    : value.ToString(),
                MaximumValue);
        }
        catch
        {
            return "[unavailable]";
        }
    }

    public static string FormatMessage(string? template, IReadOnlyList<KeyValuePair<string, object?>>? values)
    {
        if (template is null)
            return "[message template unavailable]";
        template = Limit(template, MaximumTemplate);
        var result = new StringBuilder();
        var argument = 0;
        for (var index = 0; index < template.Length && result.Length < MaximumTemplate; index++)
        {
            var current = template[index];
            if (current == '{' && index + 1 < template.Length && template[index + 1] == '{')
            {
                result.Append('{');
                index++;
                continue;
            }

            if (current == '}' && index + 1 < template.Length && template[index + 1] == '}')
            {
                result.Append('}');
                index++;
                continue;
            }

            if (current == '{' && values is not null && argument < Math.Min(values.Count, MaximumParameters))
            {
                var end = template.IndexOf('}', index + 1);
                if (end > index)
                {
                    var token = template.AsSpan(index + 1, end - index - 1);
                    var colon = token.IndexOf(':');
                    result.Append(SafeFormat(values[argument++].Value,
                        colon >= 0 ? token[(colon + 1)..].ToString() : null));
                    index = end;
                    continue;
                }
            }

            result.Append(current);
        }

        return Limit(result.ToString(), MaximumTemplate);
    }

    private static LogExceptionInfo CaptureException(Exception error,
        int depth,
        HashSet<Exception> seen,
        TextBudget budget,
        ref int remaining)
    {
        var type = error.GetType();
        var name = budget.Take(type.FullName ?? type.Name, 512, out var typeTruncated);
        if (remaining-- <= 0 || depth >= 4)
            return new(name, "[exception limit]", null, [], IsTruncated: true);
        if (!seen.Add(error))
            return new(name, "[exception cycle]", null, [], IsCycle: true);
        var children = new List<LogExceptionInfo>();
        var truncated = typeTruncated;
        // AggregateException.Message recursively formats its children, so never evaluate it.
        var message = error is AggregateException aggregate
            ? $"Aggregate exception ({aggregate.InnerExceptions.Count} inner exceptions)."
            : InheritsBaseGetter(type, nameof(Exception.Message))
                ? ReadSafe(() => error.Message, 2048)
                : "[overridden exception message unavailable]";
        var stack = InheritsBaseGetter(type, nameof(Exception.StackTrace))
            ? ReadSafe(() => error.StackTrace, 4096)
            : null;
        message = budget.Take(message, 2048, out var messageTruncated);
        var stackTruncated = false;
        if (stack is not null)
            stack = budget.Take(stack, 4096, out stackTruncated);
        truncated |= messageTruncated || stackTruncated;
        IEnumerable<Exception> inner = error is AggregateException group ? group.InnerExceptions :
            error.InnerException is { } cause ? [cause] : [];
        foreach (var child in inner)
        {
            if (remaining <= 0 || children.Count >= MaximumExceptions)
            {
                truncated = true;
                break;
            }

            children.Add(CaptureException(child, depth + 1, seen, budget, ref remaining));
        }

        return new LogExceptionInfo(name, message, stack, children, truncated);
    }

    private static string ReadSafe(Func<string?> read, int maximum)
    {
        try
        {
            return Limit(read(), maximum);
        }
        catch
        {
            return "[unavailable]";
        }
    }

    private static bool InheritsBaseGetter(Type type, string property) =>
        type.GetProperty(property)?.GetMethod?.DeclaringType == typeof(Exception);

    public static bool Matches(LogFilter? filter, string category, string message, LogDetails details) =>
        filter is null ||
        Contains(category, filter.Category) &&
        (Contains(message, filter.Message) || Contains(details.MessageTemplate, filter.Message)) &&
        (string.IsNullOrWhiteSpace(filter.ParameterName) && string.IsNullOrWhiteSpace(filter.ParameterValue) ||
         details.Parameters.Any(value => Contains(value.Name, filter.ParameterName) &&
                                         Contains(value.State == LogValueState.Null ? "null" : value.Value,
                                             filter.ParameterValue)));

    private static bool Contains(string? value, string? query) => string.IsNullOrWhiteSpace(query) ||
                                                                  value?.Contains(query.Trim(),
                                                                      StringComparison.OrdinalIgnoreCase) == true;

    internal static string Limit(string? value, int maximum) =>
        value is null ? "" : value.Length <= maximum ? value : value[..maximum] + "…";

    private sealed class TextBudget(int remaining)
    {
        public bool IsTruncated { get; private set; }

        public string Take(string? value, int maximum, out bool truncated)
        {
            truncated = value?.Length > maximum;
            var text = Limit(value, maximum);
            var size = JsonEncodedText.Encode(text, ProtocolJson.Options.Encoder).EncodedUtf8Bytes.Length;
            if (size <= remaining)
            {
                remaining -= size;
                return text;
            }

            IsTruncated = truncated = true;
            const string suffix = "…";
            var suffixBytes = JsonEncodedText.Encode(suffix, ProtocolJson.Options.Encoder).EncodedUtf8Bytes.Length;
            if (remaining < suffixBytes)
                return "";
            var low = 0;
            var high = text.Length;
            while (low < high)
            {
                var middle = low + (high - low + 1) / 2;
                var prefixBytes = JsonEncodedText.Encode(text.AsSpan(0, middle), ProtocolJson.Options.Encoder)
                    .EncodedUtf8Bytes.Length;
                if (prefixBytes + suffixBytes <= remaining)
                    low = middle;
                else
                    high = middle - 1;
            }

            if (low > 0 && low < text.Length && char.IsHighSurrogate(text[low - 1]) && char.IsLowSurrogate(text[low]))
                low--;
            var result = text[..low] + suffix;
            remaining -= JsonEncodedText.Encode(result, ProtocolJson.Options.Encoder).EncodedUtf8Bytes.Length;
            return result;
        }
    }
}
