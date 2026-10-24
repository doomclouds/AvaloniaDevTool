using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AvaloniaDevTool.Agent;

internal static class PropertyValues
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static bool CanConvert(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(string) || type == typeof(bool) || type.IsEnum ||
            type == typeof(double) || type == typeof(float) || type == typeof(decimal) ||
            type == typeof(int) || type == typeof(long) || type == typeof(short) ||
            type == typeof(uint) || type == typeof(ulong) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(ushort) ||
            type == typeof(Thickness) || type == typeof(CornerRadius) || type == typeof(Color) ||
            typeof(IBrush).IsAssignableFrom(type) || type == typeof(GridLength);
    }

    public static object? Parse(string text, Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null && string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
            return null;
        type = nullable ?? type;
        if (type == typeof(string)) return text;
        if (type == typeof(Thickness)) return Thickness.Parse(text);
        if (type == typeof(CornerRadius)) return CornerRadius.Parse(text);
        if (type == typeof(Color)) return Color.Parse(text);
        if (typeof(IBrush).IsAssignableFrom(type)) return Brush.Parse(text);
        if (type == typeof(GridLength)) return GridLength.Parse(text);
        if (type.IsEnum)
        {
            var value = Enum.Parse(type, text, true);
            if (type.GetCustomAttributes(typeof(FlagsAttribute), false).Length == 0 && !Enum.IsDefined(type, value))
                throw new FormatException("The value is not a defined enum member.");
            return value;
        }
        var converter = TypeDescriptor.GetConverter(type);
        return converter.ConvertFrom(null, Culture, text);
    }

    public static string? Format(object? value) => value switch
    {
        null => null,
        IFormattable formatted => formatted.ToString(null, Culture),
        _ => value.ToString()
    };
}

