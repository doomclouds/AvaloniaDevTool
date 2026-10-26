using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Data;
using Avalonia.Diagnostics;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private readonly ConditionalWeakTable<AvaloniaObject, Dictionary<AvaloniaProperty, IDisposable>> _overrides = new();

    private void RegisterPropertiesMethods()
    {
        Map<PropertiesRequest>(Methods.PropertiesGet, request => GetProperties(request));
        Map<PropertyPreviewRequest>(PropertyPresentationMethods.Preview, request => GetPropertyPreview(request));
        Map<SetPropertyRequest>(Methods.PropertiesSet, request => SetProperty(request));
        Map<OfficialSetPropertyRequest>(OfficialPropertyMethods.Set, request => SetOfficialProperty(request));
        Map<SetBrushRequest>(BrushMethods.Set, request => SetBrush(request));
        Map<ClearPropertyRequest>(Methods.PropertiesClear, request => ClearProperty(request));
    }

    private static string PropertyId(AvaloniaProperty property) =>
        (property.OwnerType.FullName ?? property.OwnerType.Name) + ":" + property.Name;

    private IEnumerable<AvaloniaProperty> RegisteredProperties(AvaloniaObject value) =>
        AvaloniaPropertyRegistry.Instance.GetRegistered(value.GetType())
            .Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(value.GetType()))
            .Distinct().OrderBy(x => x.Name).ThenBy(x => x.OwnerType.FullName);

    private IReadOnlyList<PropertyInfoDto> GetProperties(PropertiesRequest request)
    {
        var id = request.ObjectId;
        var target = GetAvaloniaObject(id);
        Observe(target);
        _propertyContextParents[id] = PropertyContextParentId(target);
        var result = new List<PropertyInfoDto>();
        var registered = RegisteredProperties(target).ToArray();
        var clrProperties = PropertyPresentationAdapter.PublicProperties(target.GetType()).ToArray();
        var wrapped = clrProperties.GroupBy(member => member.Name, StringComparer.Ordinal)
            .Select(group => PropertyPresentationAdapter.WrapperProperty(group.First(), registered))
            .OfType<AvaloniaProperty>().ToHashSet();
        foreach (var property in registered)
        {
            var context = ContextualProperties.Describe(target, property);
            if (request.Contextual && context?.IsApplicable == false)
                continue;
            var propertyId = PropertyId(property);
            var readOnly = property.IsReadOnly || property.IsDirect;
            var canEdit = !readOnly && PropertyValues.CanConvert(property.PropertyType);
            var hasOverride = HasOverride(target, property);
            try
            {
                var diagnostic = target.GetDiagnostic(property);
                var clrWrapper = clrProperties.FirstOrDefault(member => member.Name == property.Name &&
                                                                        member.DeclaringType == property.OwnerType &&
                                                                        member.PropertyType == property.PropertyType);
                var presentation = WithPreviewLease(id,
                        propertyId,
                        diagnostic.Value,
                        PropertyPresentationAdapter.Describe(property.PropertyType,
                            diagnostic.Value,
                            canEdit,
                            "Avalonia",
                            PropertyPresentationAdapter.Category(clrWrapper, property.OwnerType.Name))) with
                    {
                        Context = context
                    };
                canEdit &= presentation.Editor != PropertyEditorKind.ReadOnly;
                result.Add(new PropertyInfoDto(propertyId,
                    property.Name,
                    property.OwnerType.FullName ?? property.OwnerType.Name,
                    property.PropertyType.FullName ?? property.PropertyType.Name,
                    PropertyPresentationAdapter.Format(diagnostic.Value),
                    hasOverride ? "DiagnosticOverride" : diagnostic.Priority.ToString(),
                    readOnly || !canEdit,
                    canEdit,
                    hasOverride,
                    Presentation: presentation,
                    HasClrWrapper: wrapped.Contains(property)));
            }
            catch (Exception ex)
            {
                result.Add(new PropertyInfoDto(propertyId,
                    property.Name,
                    property.OwnerType.Name,
                    property.PropertyType.Name,
                    null,
                    "Unavailable",
                    true,
                    false,
                    false,
                    PropertyErrors.Describe(ex),
                    HasClrWrapper: wrapped.Contains(property)));
            }
        }

        foreach (var property in clrProperties.Where(property =>
                     request.IncludeClr && !PropertyPresentationAdapter.IsBackingWrapper(property, registered)))
        {
            var propertyId = PropertyPresentationAdapter.ClrId(property);
            try
            {
                var value = property.GetValue(target);
                var presentation = WithPreviewLease(id,
                    propertyId,
                    value,
                    PropertyPresentationAdapter.Describe(property.PropertyType,
                        value,
                        false,
                        "CLR",
                        PropertyPresentationAdapter.Category(property, property.DeclaringType!.Name)));
                result.Add(new PropertyInfoDto(propertyId,
                    property.Name,
                    property.DeclaringType!.FullName!,
                    property.PropertyType.FullName ?? property.PropertyType.Name,
                    PropertyPresentationAdapter.Format(value),
                    "CLR",
                    true,
                    false,
                    false,
                    Presentation: presentation));
            }
            catch (Exception exception)
            {
                result.Add(new PropertyInfoDto(propertyId,
                    property.Name,
                    property.DeclaringType!.FullName!,
                    property.PropertyType.FullName ?? property.PropertyType.Name,
                    null,
                    "Unavailable",
                    true,
                    false,
                    false,
                    PropertyErrors.Describe(exception),
                    new PropertyPresentation(property.DeclaringType.Name, "CLR", PropertyEditorKind.ReadOnly)));
            }
        }

        return result;
    }

    private AvaloniaObject GetAvaloniaObject(long id) =>
        (id == 0 ? _application : _objects.Get(id)) as AvaloniaObject ??
        throw new RpcException("unsupported_object", "The selected object has no Avalonia properties.");

    private AvaloniaProperty GetProperty(AvaloniaObject target, string id)
    {
        var property = RegisteredProperties(target).FirstOrDefault(x => PropertyId(x) == id);
        return property ??
               throw new RpcException("property_not_found", "The property is not registered for this object.");
    }

    private OperationResult SetProperty(SetPropertyRequest request)
    {
        var target = GetAvaloniaObject(request.ObjectId);
        var property = GetProperty(target, request.PropertyId);
        if (property.IsReadOnly || property.IsDirect)
            throw new RpcException("readonly_property", "Direct and read-only properties cannot be safely overridden.");
        if (!PropertyValues.CanConvert(property.PropertyType))
            throw new RpcException("unsupported_type", "This property type has no text editor.");
        if (PropertyPresentationAdapter.Describe(property.PropertyType, target.GetValue(property), true, "Avalonia", "")
                .Editor == PropertyEditorKind.ReadOnly)
            throw new RpcException("unsupported_type", "This brush type has no safe editor.");
        object? value;
        try
        {
            value = PropertyValues.Parse(request.Value, property.PropertyType);
            if (!property.IsValidValue(value))
                throw new FormatException("The value is rejected by the property.");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException
                                       or NotSupportedException)
        {
            throw new RpcException("conversion_failed", ex.Message);
        }

        // An animation-priority entry is disposable and leaves the underlying local binding intact.
        var entries = _overrides.GetOrCreateValue(target);
        var previous = entries.GetValueOrDefault(property);
        IDisposable? replacement;
        try
        {
            replacement = target.SetValue(property, value, BindingPriority.Animation);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException)
        {
            throw new RpcException("invalid_value", ex.Message);
        }

        if (replacement is null)
            throw new RpcException("unsupported_property", "The framework did not provide a reversible override.");
        entries[property] = replacement;
        previous?.Dispose();
        _pendingProperties.Add(request.ObjectId);
        return new OperationResult();
    }

    private OperationResult ClearProperty(ClearPropertyRequest request)
    {
        var target = GetAvaloniaObject(request.ObjectId);
        var property = GetProperty(target, request.PropertyId);
        if (_overrides.TryGetValue(target, out var entries) && entries.Remove(property, out var entry))
            entry.Dispose();
        _pendingProperties.Add(request.ObjectId);
        return new OperationResult();
    }

    private bool HasOverride(AvaloniaObject target, AvaloniaProperty property) =>
        _overrides.TryGetValue(target, out var entries) && entries.ContainsKey(property);
}
