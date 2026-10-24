using System.Reflection;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private readonly Dictionary<(long ObjectId, string PropertyId), PropertyValueLease> _propertyValueLeases = [];
    private long _propertyValueVersion;

    private PropertyPresentation WithPreviewLease(long objectId, string propertyId, object? value, PropertyPresentation presentation)
    {
        if (presentation.Preview == PropertyPreviewKind.None || value is null) return presentation;
        if (_propertyValueLeases.Count >= 1024)
            foreach (var key in _propertyValueLeases.Keys.Take(256).ToArray()) _propertyValueLeases.Remove(key);
        var version = ++_propertyValueVersion;
        var valueId = _objects.GetId(value);
        _propertyValueLeases[(objectId, propertyId)] = new PropertyValueLease(new WeakReference<object>(value), valueId, version);
        return presentation with { ValueId = valueId, Version = version };
    }

    private PropertyPreviewResult GetPropertyPreview(PropertyPreviewRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (request.MaxWidth is < 1 or > 512 || request.MaxHeight is < 1 or > 512)
            return new(PropertyPreviewStatus.InvalidValue, Message: "预览尺寸必须介于 1 和 512 像素之间。");
        if (!_propertyValueLeases.TryGetValue((request.ObjectId, request.PropertyId), out var lease) ||
            lease.Version != request.Version || lease.ValueId != request.ValueId || !lease.Value.TryGetTarget(out var value))
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
            else current = target.GetValue(GetProperty(target, request.PropertyId));
            if (!ReferenceEquals(current, value))
                return new(PropertyPreviewStatus.Stale, Message: "属性值已更换，请刷新属性。");
            var bounds = value switch { IImage image => new Rect(image.Size), Geometry geometry => geometry.Bounds, _ => default };
            if (!double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
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
                    using var transform = context.PushTransform(new Matrix(scale, 0, 0, scale, -bounds.X * scale, -bounds.Y * scale));
                    context.DrawGeometry(Brushes.DodgerBlue, null, geometry);
                }
                else return new(PropertyPreviewStatus.NotAvailable, Message: "属性类型不支持预览。");
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
}
