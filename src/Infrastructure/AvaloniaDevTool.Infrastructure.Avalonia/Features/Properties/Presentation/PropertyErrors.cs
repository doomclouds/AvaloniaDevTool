using System.Globalization;
using System.Reflection;

namespace AvaloniaDevTool.Agent;

internal static class PropertyErrors
{
    private const int MaximumLength = 2048;
    private const int MaximumDepth = 8;
    private const string Truncation = " [truncated]";

    public static string Describe(Exception error, string prefix = "")
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        for (var depth = 0; depth < MaximumDepth; depth++)
        {
            if (!seen.Add(error))
                return Limit(prefix, "Exception cycle; details unavailable." + Truncation);
            // AggregateException.Message expands all descendants before a caller could truncate it.
            if (error is AggregateException aggregate)
                return Limit(prefix,
                    "AggregateException (" + aggregate.InnerExceptions.Count.ToString(CultureInfo.InvariantCulture) +
                    " inner exceptions; nested messages unavailable).");
            if (error.InnerException is not { } inner)
                return Limit(prefix, Message(error));
            // InnerException is nonvirtual. Never invoke an arbitrary GetBaseException override.
            error = inner;
        }

        return Limit(prefix, "Inner exception depth limit reached; details unavailable." + Truncation);
    }

    private static string Message(Exception error)
    {
        try
        {
            var getter = error.GetType().GetProperty(nameof(Exception.Message),
                BindingFlags.Public | BindingFlags.Instance,
                null,
                typeof(string),
                Type.EmptyTypes,
                null)?.GetMethod;
            if (getter?.DeclaringType != typeof(Exception))
                return "[Exception.Message override unavailable; the override was not executed.]";
            return error.Message;
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            return "[Exception.Message unavailable; its getter metadata or stored message could not be read.]";
        }
    }

    private static string Limit(string prefix, string message)
    {
        if (prefix.Length + message.Length <= MaximumLength)
            return prefix + message;
        var length = MaximumLength - prefix.Length - Truncation.Length;
        if (length > 0 && char.IsHighSurrogate(message[length - 1]) && char.IsLowSurrogate(message[length]))
            length--;
        return prefix + message[..Math.Max(0, length)] + Truncation;
    }
}
