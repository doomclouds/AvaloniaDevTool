using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class PropertyPresentationAdapter
{
    public static IEnumerable<PropertyInfo> PublicProperties(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.Instance |
                                                           BindingFlags.DeclaredOnly))
                if (property.GetMethod is { IsPublic: true, IsStatic: false } &&
                    property.GetIndexParameters().Length == 0)
                    yield return property;
    }

    public static string ClrId(PropertyInfo property) =>
        "clr:" + property.DeclaringType!.FullName + ":" + property.Name;

    public static AvaloniaProperty? WrapperProperty(PropertyInfo property, IReadOnlyList<AvaloniaProperty> registered)
    {
        var field = property.DeclaringType!.GetField(property.Name + "Property",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (field?.GetValue(null) is not AvaloniaProperty avalonia || !registered.Contains(avalonia) ||
            avalonia.PropertyType != property.PropertyType)
            return null;
        // Managed targets can verify the getter's field use. AOT retains the declared
        // property/field metadata but has no IL method body to inspect.
        return !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ||
               IsBackingWrapper(property, registered)
            ? avalonia
            : null;
    }

    // Name equality alone cannot identify a wrapper: a derived CLR member may hide an Avalonia property.
    public static bool IsBackingWrapper(PropertyInfo property, IReadOnlyList<AvaloniaProperty> registered)
    {
        var field = property.DeclaringType!.GetField(property.Name + "Property",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (field?.GetValue(null) is not AvaloniaProperty avalonia || !registered.Contains(avalonia) ||
            avalonia.PropertyType != property.PropertyType)
            return false;
        if (avalonia.IsDirect && avalonia.GetType().GetProperty("Getter")?.GetValue(avalonia) is Delegate getter)
        {
            var getterBody = getter.Method.GetMethodBody()?.GetILAsByteArray();
            if (getterBody is not null)
                for (var index = 0; index + 4 < getterBody.Length; index++)
                    if (getterBody[index] is 0x28 or 0x6f)
                        try
                        {
                            if (getter.Method.Module.ResolveMethod(BitConverter.ToInt32(getterBody, index + 1)) ==
                                property.GetMethod)
                                return true;
                        }
                        catch (ArgumentException)
                        {
                        }
        }

        var method = property.GetMethod!;
        var body = method.GetMethodBody()?.GetILAsByteArray();
        if (body is null)
            return false;
        for (var index = 0; index + 4 < body.Length; index++)
        {
            if (body[index] != 0x7e)
                continue; // ldsfld
            try
            {
                var loaded = method.Module.ResolveField(BitConverter.ToInt32(body, index + 1),
                    property.DeclaringType.GetGenericArguments(),
                    null);
                if (loaded == field)
                    return true;
            }
            catch (ArgumentException)
            {
            }
        }

        return false;
    }

    public static PropertyPresentation Describe(Type type, object? value, bool editable, string kind, string category)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        var brush = BrushValues.Describe(type, value);
        var preview = value is IImage || typeof(IImage).IsAssignableFrom(type) ? PropertyPreviewKind.Image :
            value is Geometry || typeof(Geometry).IsAssignableFrom(type) ? PropertyPreviewKind.Geometry :
            PropertyPreviewKind.None;
        var editor = !editable
            ? PropertyEditorKind.ReadOnly
            : type == typeof(bool)
                ? PropertyEditorKind.Boolean
                : type.IsEnum
                    ? (type.IsDefined(typeof(FlagsAttribute)) ? PropertyEditorKind.Flags : PropertyEditorKind.Enum)
                    : type == typeof(Color)
                        ? PropertyEditorKind.Color
                        : typeof(IBrush).IsAssignableFrom(type) && type.IsAssignableFrom(typeof(SolidColorBrush)) &&
                          value is null or ISolidColorBrush
                            ? PropertyEditorKind.SolidColorBrush
                            : brush is not null
                                ? PropertyEditorKind.Brush
                                : IsNumeric(type)
                                    ? PropertyEditorKind.Number
                                    : PropertyEditorKind.Text;
        string? notice = preview != PropertyPreviewKind.None && value is null ? "属性值为 null，无法预览。" : null;
        if (typeof(IBrush).IsAssignableFrom(type) &&
            brush is null)
        {
            editor = PropertyEditorKind.ReadOnly;
            notice = "此画刷没有安全编辑器：ImageBrush、DrawingBrush、着色器、自定义画刷及超过 256 个色标的渐变仅供查看。";
        }

        var choices = type.IsEnum
            ? Enum.GetNames(type).Select(name =>
                new PropertyEnumChoice(name, Enum.Format(type, Enum.Parse(type, name), "D"))).ToArray()
            : null;
        return new PropertyPresentation(category, kind, editor, choices, preview, Notice: notice, Brush: brush);
    }

    public static string Category(PropertyInfo? property, string fallback) =>
        property?.GetCustomAttribute<CategoryAttribute>()?.Category ?? fallback;

    private static bool IsNumeric(Type type) => Type.GetTypeCode(type) is
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or
        TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    public static string? Format(object? value) => value switch
    {
        null => null,
        string text => text.Length <= 4096 ? text : text[..4096] + "…",
        bool boolean => boolean ? "True" : "False",
        Enum enumeration => enumeration.ToString(),
        Color color => color.ToString(),
        ISolidColorBrush brush => brush.Color.ToString(),
        Thickness thickness => thickness.ToString(),
        CornerRadius radius => radius.ToString(),
        GridLength length => length.ToString(),
        Point point => point.ToString(),
        Size size => size.ToString(),
        Rect rect => rect.ToString(),
        Vector vector => vector.ToString(),
        Matrix matrix => matrix.ToString(),
        byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture),
        _ => "{" + (value.GetType().FullName ?? value.GetType().Name) + "}"
    };
}
