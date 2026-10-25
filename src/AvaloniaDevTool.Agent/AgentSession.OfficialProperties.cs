using Avalonia;
using Avalonia.Data;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private OperationResult SetOfficialProperty(OfficialSetPropertyRequest request)
    {
        if (request.Value is null)
            throw new RpcException("invalid_request", "value must be a string; use the literal 'null' to assign null.");
        var target = GetAvaloniaObject(request.ObjectId);
        var property = GetProperty(target, request.PropertyId);
        if (property.IsReadOnly || property.IsDirect)
            throw new RpcException("readonly_property", "Direct and read-only properties cannot be safely overridden.");
        if (!request.IsXamlValue && request.Value == "unset")
            return ClearProperty(new ClearPropertyRequest(request.ObjectId, request.PropertyId));
        object? value;
        try
        {
            if (request.IsXamlValue)
                value = OfficialXamlValueConverter.Parse(request.Value, target.GetType());
            else if (request.Value == "null")
            {
                if (property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null)
                    throw new FormatException("Null is only supported for reference or nullable value types.");
                value = null;
            }
            else
            {
                if (!PropertyValues.CanConvert(property.PropertyType))
                    throw new RpcException("unsupported_type", "This property type requires an XAML object value.");
                value = PropertyValues.Parse(request.Value, property.PropertyType);
            }
            if (value is not null && !property.PropertyType.IsInstanceOfType(value))
                throw new FormatException($"The value type {value.GetType().FullName} is incompatible with {property.PropertyType.FullName}.");
            if (!property.IsValidValue(value))
                throw new FormatException("The value is rejected by the property.");
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException or NotSupportedException)
        {
            throw new RpcException("conversion_failed", PropertyErrors.Describe(error));
        }

        var entries = _overrides.GetOrCreateValue(target);
        var previous = entries.GetValueOrDefault(property);
        IDisposable? replacement;
        try { replacement = target.SetValue(property, value, BindingPriority.Animation); }
        catch (Exception error) when (error is ArgumentException or InvalidCastException)
        { throw new RpcException("invalid_value", PropertyErrors.Describe(error)); }
        if (replacement is null)
            throw new RpcException("unsupported_property", "The framework did not provide a reversible override.");
        entries[property] = replacement;
        previous?.Dispose();
        _pendingProperties.Add(request.ObjectId);
        return new OperationResult();
    }

    private StyleSnapshot GetOfficialStyles(OfficialStylesRequest request)
    {
        var target = GetAvaloniaObject(request.ObjectId);
        bool Included(string name, string declaringType) => request.PropertyNames is null || request.PropertyNames.Any(
            item => item is not null && (item == name || item == declaringType + ":" + name));
        var includedProperties = RegisteredProperties(target).Where(property => Included(property.Name,
            property.OwnerType.FullName ?? property.OwnerType.Name)).ToArray();
        var result = Avalonia12DiagnosticsAdapter.Inspect(target, _objects.GetId);
        var frames = result.Frames.Select(frame => frame with
        {
            Setters = frame.Setters.Where(setter => Included(setter.PropertyName, setter.DeclaringType)).ToArray()
        }).Where(frame => frame.Setters.Count > 0).ToList();
        if (request.IncludeDefaults)
        {
            var defaults = new List<StyleSetterInfo>();
            foreach (var property in includedProperties.Where(property => !property.IsDirect))
            {
                if (property.GetMetadata(target) is not IStyledPropertyMetadata metadata) continue;
                var value = metadata.DefaultValue;
                defaults.Add(new StyleSetterInfo(property.Name, property.OwnerType.FullName ?? property.OwnerType.Name,
                    PropertyValues.Format(value), Equals(target.GetValue(property), value)));
            }
            if (defaults.Count > 0) frames.Add(new StyleFrameInfo("Property metadata defaults", "Default", "Unset", true, defaults));
        }
        return result with
        {
            Frames = frames,
            Bindings = result.Bindings.Where(binding => request.PropertyNames is null ||
                includedProperties.Any(property => property.Name == binding.PropertyName)).ToArray()
        };
    }
}
