using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Data;
using Avalonia.Diagnostics;
using AvaloniaDevTool.Protocol;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace AvaloniaDevTool.Agent;

internal sealed class PropertyInspectionService : IDisposable
{
    private readonly Application _application;
    private readonly WeakObjectRegistry _objects;
    private readonly ObjectObservationService _observations;
    private readonly Action<string, object> _notify;
    private readonly HashSet<long> _pendingProperties = [];
    private bool _disposed;

    internal PropertyInspectionService(Application application,
        WeakObjectRegistry objects,
        ObjectObservationService observations,
        Action<string, object> notify)
    {
        _application = application;
        _objects = objects;
        _observations = observations;
        _notify = notify;
        observations.PropertyChanged += MarkChanged;
    }

    private long Observe(object value) => _observations.Observe(value);

    internal void MarkChanged(long id)
    {
        if (!_disposed)
            _pendingProperties.Add(id);
    }

    internal void FlushChanges()
    {
        if (_disposed)
            return;
        RefreshPropertyContexts();
        foreach (var id in _pendingProperties.ToArray())
            _notify(Methods.PropertiesChanged, new PropertiesChangedNotification(id));
        _pendingProperties.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _observations.PropertyChanged -= MarkChanged;
        foreach (var target in _overrides)
        {
            foreach (var entry in target.Value.Values)
                entry.Dispose();
            target.Value.Clear();
        }

        _overrides.Clear();
        _propertyValueLeases.Clear();
        _propertyContextParents.Clear();
        _pendingProperties.Clear();
    }

    private readonly ConditionalWeakTable<AvaloniaObject, Dictionary<AvaloniaProperty, IDisposable>> _overrides = new();

    internal void Register(RpcMethodRegistry routes)
    {
        routes.Map<PropertiesRequest>(Methods.PropertiesGet, request => GetProperties(request));
        routes.Map<PropertyPreviewRequest>(PropertyPresentationMethods.Preview, request => GetPropertyPreview(request));
        routes.Map<SetPropertyRequest>(Methods.PropertiesSet, request => SetProperty(request));
        routes.Map<SetPropertyValueRequest>(PropertyValueMethods.Set, request => SetOfficialProperty(request));
        routes.Map<SetBrushRequest>(BrushMethods.Set, request => SetBrush(request));
        routes.Map<ClearPropertyRequest>(Methods.PropertiesClear, request => ClearProperty(request));
    }

    internal static string PropertyId(AvaloniaProperty property) =>
        (property.OwnerType.FullName ?? property.OwnerType.Name) + ":" + property.Name;

    internal IEnumerable<AvaloniaProperty> RegisteredProperties(AvaloniaObject value) =>
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

    internal AvaloniaObject GetAvaloniaObject(long id) =>
        (id == 0 ? _application : _objects.Get(id)) as AvaloniaObject ??
        throw new RpcException("unsupported_object", "The selected object has no Avalonia properties.");

    private AvaloniaProperty GetProperty(AvaloniaObject target, string id)
    {
        var property = RegisteredProperties(target).FirstOrDefault(x => PropertyId(x) == id);
        return property ??
               throw new RpcException("property_not_found", "The property is not registered for this object.");
    }

    internal OperationResult SetProperty(SetPropertyRequest request)
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

    private readonly Dictionary<(long ObjectId, string PropertyId), PropertyValueLease> _propertyValueLeases = [];

    private long _propertyValueVersion;

    private PropertyPresentation WithPreviewLease(long objectId,
        string propertyId,
        object? value,
        PropertyPresentation presentation)
    {
        if (presentation.Preview == PropertyPreviewKind.None || value is null)
            return presentation;
        if (_propertyValueLeases.Count >= 1024)
            foreach (var key in _propertyValueLeases.Keys.Take(256).ToArray())
                _propertyValueLeases.Remove(key);
        var version = ++_propertyValueVersion;
        var valueId = _objects.GetId(value);
        _propertyValueLeases[(objectId, propertyId)] =
            new PropertyValueLease(new WeakReference<object>(value), valueId, version);
        return presentation with { ValueId = valueId, Version = version };
    }

    private PropertyPreviewResult GetPropertyPreview(PropertyPreviewRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (request.MaxWidth is < 1 or > 512 || request.MaxHeight is < 1 or > 512)
            return new(PropertyPreviewStatus.InvalidValue, Message: "预览尺寸必须介于 1 和 512 像素之间。");
        if (!_propertyValueLeases.TryGetValue((request.ObjectId, request.PropertyId), out var lease) ||
            lease.Version != request.Version || lease.ValueId != request.ValueId ||
            !lease.Value.TryGetTarget(out var value))
            return new(PropertyPreviewStatus.Stale, Message: "属性值已更新或释放，请刷新属性。");
        try
        {
            var target = GetAvaloniaObject(request.ObjectId);
            object? current;
            if (request.PropertyId.StartsWith("clr:", StringComparison.Ordinal))
            {
                var property = PropertyPresentationAdapter.PublicProperties(target.GetType())
                    .FirstOrDefault(property => PropertyPresentationAdapter.ClrId(property) == request.PropertyId);
                current = property?.GetValue(target);
            }
            else
                current = target.GetValue(GetProperty(target, request.PropertyId));

            if (!ReferenceEquals(current, value))
                return new(PropertyPreviewStatus.Stale, Message: "属性值已更换，请刷新属性。");
            var bounds = value switch
            {
                IImage image => new Rect(image.Size), Geometry geometry => geometry.Bounds, _ => default
            };
            if (!double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || bounds.Width <= 0 ||
                bounds.Height <= 0)
                return new(PropertyPreviewStatus.NotAvailable, Message: "属性值没有可预览的有限正尺寸。");
            var scale = Math.Min(1d, Math.Min(request.MaxWidth / bounds.Width, request.MaxHeight / bounds.Height));
            var width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            using (var context = bitmap.CreateDrawingContext())
            {
                if (value is IImage image)
                    context.DrawImage(image, new Rect(image.Size), new Rect(new Size(width, height)));
                else if (value is Geometry geometry)
                {
                    using var transform =
                        context.PushTransform(new Matrix(scale, 0, 0, scale, -bounds.X * scale, -bounds.Y * scale));
                    context.DrawGeometry(Brushes.DodgerBlue, null, geometry);
                }
                else
                    return new(PropertyPreviewStatus.NotAvailable, Message: "属性类型不支持预览。");
            }

            using var stream = new MemoryStream();
            bitmap.Save(stream, new PngBitmapEncoderOptions());
            if (stream.Length > 1024 * 1024)
                return new(PropertyPreviewStatus.NotAvailable, Message: "预览超过 1 MiB 上限。");
            return new(PropertyPreviewStatus.Available, Convert.ToBase64String(stream.ToArray()), width, height);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(PropertyPreviewStatus.NotAvailable, Message: PropertyErrors.Describe(exception, "无法绘制该属性值："));
        }
    }

    private sealed record PropertyValueLease(WeakReference<object> Value, long ValueId, long Version);

    private readonly Dictionary<long, long?> _propertyContextParents = [];

    private long? PropertyContextParentId(AvaloniaObject target) =>
        ContextualProperties.Parent(target) is { } parent ? _objects.GetId(parent) : null;

    private void RefreshPropertyContexts()
    {
        foreach (var entry in _propertyContextParents.ToArray())
        {
            if (!_observations.TryGet(entry.Key, out var target))
            {
                _propertyContextParents.Remove(entry.Key);
                continue;
            }

            var parent = PropertyContextParentId(target);
            if (parent == entry.Value)
                continue;
            _propertyContextParents[entry.Key] = parent;
            _pendingProperties.Add(entry.Key);
        }
    }

    private OperationResult SetBrush(SetBrushRequest request)
    {
        if (request.Brush is null)
            throw new RpcException("invalid_value", "A brush draft is required.");
        var target = GetAvaloniaObject(request.ObjectId);
        var property = GetProperty(target, request.PropertyId);
        if (property.IsReadOnly || property.IsDirect)
            throw new RpcException("readonly_property", "This property cannot be safely overridden.");
        if (BrushValues.Describe(property.PropertyType, target.GetValue(property)) is null)
            throw new RpcException("unsupported_type",
                "Image, drawing, shader and opaque brushes do not have a safe editor.");
        Avalonia.Media.Brush brush;
        try
        {
            brush = BrushValues.Create(request.Brush, property.PropertyType);
            if (!property.IsValidValue(brush))
                throw new FormatException("The property rejects this brush.");
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
        {
            throw new RpcException("conversion_failed", PropertyErrors.Describe(exception));
        }

        var entries = _overrides.GetOrCreateValue(target);
        var previous = entries.GetValueOrDefault(property);
        var replacement = target.SetValue(property, brush, BindingPriority.Animation);
        if (replacement is null)
            throw new RpcException("unsupported_property", "The framework did not provide a reversible override.");
        entries[property] = replacement;
        previous?.Dispose();
        _pendingProperties.Add(request.ObjectId);
        return new OperationResult();
    }

    private OperationResult SetOfficialProperty(SetPropertyValueRequest request)
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
                value = XamlPropertyValueConverter.Parse(request.Value, target.GetType());
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
                throw new FormatException(
                    $"The value type {value.GetType().FullName} is incompatible with {property.PropertyType.FullName}.");
            if (!property.IsValidValue(value))
                throw new FormatException("The value is rejected by the property.");
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException
                                          or NotSupportedException)
        {
            throw new RpcException("conversion_failed", PropertyErrors.Describe(error));
        }

        var entries = _overrides.GetOrCreateValue(target);
        var previous = entries.GetValueOrDefault(property);
        IDisposable? replacement;
        try
        {
            replacement = target.SetValue(property, value, BindingPriority.Animation);
        }
        catch (Exception error) when (error is ArgumentException or InvalidCastException)
        {
            throw new RpcException("invalid_value", PropertyErrors.Describe(error));
        }

        if (replacement is null)
            throw new RpcException("unsupported_property", "The framework did not provide a reversible override.");
        entries[property] = replacement;
        previous?.Dispose();
        _pendingProperties.Add(request.ObjectId);
        return new OperationResult();
    }
}
