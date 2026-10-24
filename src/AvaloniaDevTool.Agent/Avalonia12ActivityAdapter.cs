using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Styling;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Isolates the private Avalonia 12 activity initialization and safe selector representation.</summary>
internal static class Avalonia12ActivityAdapter
{
    public const string SourceName = "Avalonia.Diagnostic.Source";
    private static readonly object InitializationGate = new();
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void EnsureInitialized()
    {
        lock (InitializationGate)
        {
            var assembly = typeof(AvaloniaObject).Assembly;
            if (assembly.GetName().Version?.Major != 12)
                throw new RpcException("adapter_unavailable", "Activity profiling requires Avalonia 12.");
            var diagnostic = assembly.GetType("Avalonia.Diagnostics.Diagnostic");
            var source = diagnostic?.GetField("s_activitySource", BindingFlags.Static | BindingFlags.NonPublic);
            var initialize = diagnostic?.GetMethod("InitActivitySource", BindingFlags.Static | BindingFlags.Public);
            if (source is null || source.FieldType != typeof(ActivitySource) || initialize is null)
                throw new RpcException("adapter_unavailable", "The Avalonia 12 activity source initialization shape is unavailable.");
            // Reading the field runs the framework's static initializer. Never replace an existing shared source.
            // The IsEnabled switch is cached by Avalonia; changing that cached value would affect unrelated diagnostics.
            if (source.GetValue(null) is null)
                initialize.Invoke(null, null);
            if (source.GetValue(null) is not ActivitySource)
                throw new RpcException("adapter_unavailable", "Avalonia did not initialize its diagnostic activity source.");
        }
    }

    public static string? Text(object? value) => Text(value, 0);

    private static string? Text(object? value, int depth) => value switch
    {
        null => null,
        string text => Limit(text),
        bool flag => flag ? "True" : "False",
        char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Limit(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""),
        Guid guid => guid.ToString("D"),
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
        Type type when type.GetType().Assembly == typeof(Type).Assembly => Limit(type.FullName ?? type.Name),
        Style style => SelectorText(style.Selector) ?? Identity(style),
        Selector selector => SelectorText(selector),
        ThemeVariant theme when depth < 4 => Text(theme.Key, depth + 1),
        Enum result => Limit(Enum.GetName(result.GetType(), result) ?? "Unknown"),
        _ => Identity(value)
    };

    // Do not call virtual application ToString methods, even for resource keys and custom selectors.
    // Common framework selectors can be described from bounded private fields without executing application code.
    private static string? SelectorText(Selector? selector, int depth = 0)
    {
        if (selector is null) return null;
        var type = selector.GetType();
        if (depth >= 12 || type.Assembly != typeof(Selector).Assembly || type.FullName != "Avalonia.Styling.TypeNameAndClassSelector")
            return Identity(selector);
        var text = new StringBuilder(SelectorText(type.GetField("_previous", Fields)?.GetValue(selector) as Selector, depth + 1));
        if (type.GetField("_targetType", Fields)?.GetValue(selector) is Type target)
            text.Append(target.GetType().Assembly == typeof(Type).Assembly ? Limit(target.Name) : Identity(target));
        if (type.GetField("<Name>k__BackingField", Fields)?.GetValue(selector) is string name)
            text.Append('#').Append(Limit(name));
        if (type.GetField("_classes", Fields)?.GetValue(selector) is List<string> classes)
            for (var index = 0; index < Math.Min(classes.Count, 16); index++)
            {
                var item = Limit(classes[index]);
                if (!item.StartsWith(':')) text.Append('.');
                text.Append(item);
            }
        return Limit(text.ToString());
    }

    private static string Identity(object value) => "<" + Limit(value.GetType().FullName ?? value.GetType().Name, 220) +
        "#" + RuntimeHelpers.GetHashCode(value).ToString("x", CultureInfo.InvariantCulture) + ">";
    internal static string Limit(string value, int maximum = 256) => value.Length <= maximum ? value : value[..maximum];
}
