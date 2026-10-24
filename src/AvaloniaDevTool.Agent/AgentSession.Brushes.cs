using Avalonia.Data;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private OperationResult SetBrush(SetBrushRequest request)
    {
        if (request.Brush is null) throw new RpcException("invalid_value", "A brush draft is required.");
        var target = GetAvaloniaObject(request.ObjectId);
        var property = GetProperty(target, request.PropertyId);
        if (property.IsReadOnly || property.IsDirect) throw new RpcException("readonly_property", "This property cannot be safely overridden.");
        if (BrushValues.Describe(property.PropertyType, target.GetValue(property)) is null)
            throw new RpcException("unsupported_type", "Image, drawing, shader and opaque brushes do not have a safe editor.");
        Avalonia.Media.Brush brush;
        try
        {
            brush = BrushValues.Create(request.Brush, property.PropertyType);
            if (!property.IsValidValue(brush)) throw new FormatException("The property rejects this brush.");
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
        { throw new RpcException("conversion_failed", PropertyErrors.Describe(exception)); }
        var entries = _overrides.GetOrCreateValue(target);
        var previous = entries.GetValueOrDefault(property);
        var replacement = target.SetValue(property, brush, BindingPriority.Animation);
        if (replacement is null) throw new RpcException("unsupported_property", "The framework did not provide a reversible override.");
        entries[property] = replacement;
        previous?.Dispose();
        _pendingProperties.Add(request.ObjectId);
        return new OperationResult();
    }
}
