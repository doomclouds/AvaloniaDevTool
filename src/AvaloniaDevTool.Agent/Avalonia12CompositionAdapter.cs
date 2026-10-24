using System.Collections;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>
/// Version-specific boundary verified against the public Avalonia 12.1.4 source, tag 2d79ba55.
/// All server state and native drawing resources are accessed inside a post-target compositor job.
/// </summary>
internal static class Avalonia12CompositionAdapter
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly BaseAssembly = typeof(Visual).Assembly;
    private static readonly Type ServerVisualType = RequiredType("Avalonia.Rendering.Composition.Server.ServerCompositionVisual");
    private static readonly Type RenderContextType = RequiredType("Avalonia.Rendering.Composition.Server.ServerVisualRenderContext");
    private static readonly MethodInfo RenderCore = ServerVisualType.GetMethod("RenderCore", Members)!;
    private static readonly MethodInfo InvokeJob = typeof(Compositor).GetMethods(Members)
        .Single(method => method.Name == "InvokeServerJobAsync" && method.IsGenericMethodDefinition);

    public static async Task<CompositionSnapshot> CaptureAsync(Visual root, CompositionSnapshotRequest request,
        Func<object, long> observe, CancellationToken cancellationToken)
    {
        if (BaseAssembly.GetName().Version is not { Major: 12, Minor: 1 })
            throw new RpcException("adapter_unavailable", "Composition capture requires the Avalonia 12.1 adapter.");
        var composition = ElementComposition.GetElementVisual(root);
        if (composition is null || Read(composition, "Root") is null)
            throw new RpcException("renderer_unavailable", "This root has no attached composition renderer.");
        var compositor = composition.Compositor;
        var serverCompositor = Read(compositor, "Server")
            ?? throw new RpcException("adapter_unavailable", "The Avalonia compositor server is unavailable.");
        var state = new CaptureState(request, Read(composition, "Server")!, observe(root));
        try
        {
            // UI metadata is copied before the batch commit. Server transforms, order, drawing and
            // visibility are read together after that same batch has updated and rendered targets.
            var count = 0;
            foreach (var visual in new[] { root }.Concat(root.GetVisualDescendants()))
            {
                if (++count > 10000)
                    throw new RpcException("capture_limit", "The visual-to-compositor mapping exceeds 10000 nodes.");
                if (ElementComposition.GetElementVisual(visual) is not { } node || Read(node, "Server") is not { } server)
                    continue;
                if (OverlayService.IsDiagnosticVisual(visual))
                {
                    state.Excluded.Add(server);
                    continue;
                }
                state.Objects[server] = new UiInfo(observe(visual), Text(visual.GetType().FullName ?? visual.GetType().Name),
                    Text((visual as StyledElement)?.Name), visual.ZIndex, DescribeClip(visual));
            }
            Func<CompositionSnapshot> job = () => CaptureOnRenderThread(serverCompositor, state, cancellationToken);
            var task = (Task<CompositionSnapshot>)InvokeJob.MakeGenericMethod(typeof(CompositionSnapshot))
                .Invoke(compositor, [job, true])!;
            // A stopped renderer cannot hold the client request forever. The state is cleared even
            // when its already queued compositor callback has not executed yet.
            _ = task.ContinueWith(completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            try { return await task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            catch (TimeoutException)
            {
                throw new RpcException("renderer_unavailable", "The compositor did not execute capture within 10 seconds.");
            }
        }
        catch (TargetInvocationException error)
        {
            throw new RpcException("adapter_unavailable", "The Avalonia composition adapter failed: " +
                (error.InnerException?.GetType().Name ?? error.GetType().Name));
        }
        finally { state.Clear(); }
    }

    private static CompositionSnapshot CaptureOnRenderThread(object compositor, CaptureState state,
        CancellationToken cancellationToken)
    {
        lock (state.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = state.Root ?? throw new OperationCanceledException();
            Call(compositor, "VerifyAccess");
            if (Read(root, "Root") is null)
                throw new RpcException("detached_object", "The compositor root detached before capture.");
            var manager = Read(compositor, "RenderInterface")!;
            using var current = (IDisposable)Call(manager, "EnsureCurrent")!;
            var backend = Read(manager, "Value")!;
            var layers = new List<CompositionLayer>();
            var limitations = new HashSet<string>(StringComparer.Ordinal);
            var truncated = false;
            var bytes = 0;
            long pixels = 0;
            Visit(root, null, 0, 0, Matrix.Identity);
            var size = Read(root, "Size")!;
            var snapshot = new CompositionSnapshot(state.RootObjectId, Number(size, "X"), Number(size, "Y"),
                layers, layers.Count, truncated, !truncated && limitations.Count == 0, limitations.ToArray());
            // Include base64 expansion, all text metadata and the RPC envelope's reserved space.
            while (ProtocolJson.SerializeToUtf8Bytes(snapshot).Length > RpcPeer.MaximumFrameBytes - 4096)
            {
                var index = layers.FindLastIndex(layer => layer.PngBase64 is not null);
                if (index < 0) throw new RpcException("capture_limit", "Composition metadata exceeds the protocol frame limit.");
                layers[index] = layers[index] with { PngBase64 = null, ImageStatus = "ImageBudgetExceeded" };
                limitations.Add("The JSON frame budget omitted one or more layer images.");
                snapshot = snapshot with { SupportsExactComposite = false, Limitations = limitations.ToArray() };
            }
            return snapshot;

            void Visit(object node, long? parent, int depth, int childIndex, Matrix parentTransform)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (state.Excluded.Contains(node)) return;
                var own = Read(node, "OwnTransform") as Matrix? ?? Matrix.Identity;
                var relative = parent is null ? Matrix.Identity : own;
                var cumulative = relative * parentTransform;
                if (!Affine(relative) || !Affine(cumulative))
                    throw new RpcException("unsupported_transform", "Composition capture requires finite affine transforms.");
                var id = layers.Count + 1L;
                state.Objects.TryGetValue(node, out var info);
                var nodeSize = Read(node, "Size")!;
                var boundsValue = Call(node, "ComputeOwnContentBounds");
                var bounds = boundsValue is not null ? new Rect(Number(boundsValue, "Left"), Number(boundsValue, "Top"),
                    Number(boundsValue, "Width"), Number(boundsValue, "Height")) : (Rect?)null;
                if (bounds is { } raw && Finite(raw.X, raw.Y, raw.Width, raw.Height))
                {
                    var scale = state.Request.Scaling;
                    bounds = new Rect(Math.Floor(raw.X * scale) / scale, Math.Floor(raw.Y * scale) / scale,
                        (Math.Ceiling(raw.Right * scale) - Math.Floor(raw.X * scale)) / scale,
                        (Math.Ceiling(raw.Bottom * scale) - Math.Floor(raw.Y * scale)) / scale);
                }
                var hasEffect = Read(node, "Effect") is not null;
                var hasMask = Read(node, "OpacityMaskBrush") is not null;
                var hasCache = Read(node, "Cache") is not null;
                var opacity = Number(node, "Opacity");
                if (!Finite(Number(nodeSize, "X"), Number(nodeSize, "Y"), opacity))
                    throw new RpcException("invalid_layout", "A compositor node has non-finite size or opacity.");
                var clip = Read(node, "Clip");
                var descriptor = info?.Clip;
                if (clip is not null && descriptor is null && Read(clip, "Bounds") is Rect clipBounds)
                    descriptor = new CompositionClip("UnavailablePath", RectDto(clipBounds));
                if (hasEffect) limitations.Add("Effects are metadata only; layer PNGs contain drawing before effects.");
                if (hasMask) limitations.Add("Opacity masks are metadata only; layer PNGs contain drawing before opacity masks.");
                if (hasCache) limitations.Add("Bitmap caches are bypassed to isolate each node's own drawing.");
                if (opacity != 1 || relative.M11 != 1 || relative.M12 != 0 || relative.M21 != 0 || relative.M22 != 1 ||
                    relative.M31 != Math.Truncate(relative.M31) || relative.M32 != Math.Truncate(relative.M32))
                    limitations.Add("Recomposing rasterized drawing under transforms or fractional opacity can differ from native vector rendering and alpha rounding.");
                CompositionClip? boundsClip = null;
                if (Read(node, "ClipToBounds") is true)
                {
                    var radius = Read(node, "_cornerRadius") as CornerRadius? ?? default;
                    boundsClip = new CompositionClip(radius == default ? "Rectangle" : "RoundedRectangle",
                        new CompositionRect(0, 0, Number(nodeSize, "X"), Number(nodeSize, "Y")),
                        CornerRadius: radius == default ? null : radius.ToString());
                    if (radius != default)
                        limitations.Add("Rounded ClipToBounds requires the supplied corner radius when recomposing; rectangular clipping alone is approximate.");
                }
                if (clip is not null && descriptor?.Kind == "UnavailablePath")
                    limitations.Add("A geometry clip has no transferable SVG path; its bounds are metadata only.");
                var (image, width, height, status) = CaptureImage(node, bounds);
                if (status is not ("Captured" or "Empty"))
                    limitations.Add("One or more own-drawing images were omitted: " + status + ".");
                layers.Add(new CompositionLayer(id, parent, info?.ObjectId, depth, childIndex,
                    info?.TypeName ?? Text(node.GetType().FullName ?? node.GetType().Name)!, info?.Name, info?.ZIndex,
                    Number(nodeSize, "X"), Number(nodeSize, "Y"), Transform(relative), Transform(cumulative),
                    opacity, Read(node, "Visible") is true, Read(node, "ClipToBounds") is true,
                    descriptor, bounds is { } content ? RectDto(content) : null, width, height, image, status,
                    hasEffect, hasMask, hasCache, boundsClip));
                if (Read(node, "Children") is not { } collection || Read(collection, "List") is not IEnumerable children) return;
                var index = 0;
                foreach (var child in children)
                {
                    if (child is null || state.Excluded.Contains(child)) { index++; continue; }
                    if (depth >= state.Request.MaxDepth || layers.Count >= state.Request.MaxNodes)
                    {
                        truncated = true;
                        break;
                    }
                    Visit(child, id, depth + 1, index++, cumulative);
                }
            }

            (string? Image, int Width, int Height, string Status) CaptureImage(object node, Rect? content)
            {
                if (content is not { } bounds || bounds.Width == 0 || bounds.Height == 0)
                    return (null, 0, 0, "Empty");
                if (!Finite(bounds.X, bounds.Y, bounds.Width, bounds.Height) || bounds.Width < 0 || bounds.Height < 0)
                    throw new RpcException("invalid_layout", "A compositor node has invalid own drawing bounds.");
                // Align the local image rectangle to its pixel grid without dropping overflow paint.
                var scaling = state.Request.Scaling;
                var pixelWidth = Math.Ceiling(bounds.Right * scaling) - Math.Floor(bounds.X * scaling);
                var pixelHeight = Math.Ceiling(bounds.Bottom * scaling) - Math.Floor(bounds.Y * scaling);
                if (pixelWidth > 16384 || pixelHeight > 16384 || pixelWidth * pixelHeight > state.Request.MaxTotalPixels - pixels)
                    return (null, 0, 0, "PixelBudgetExceeded");
                var width = Math.Max(1, (int)pixelWidth);
                var height = Math.Max(1, (int)pixelHeight);
                pixels += (long)width * height;
                var remaining = Math.Min(state.Request.MaxImageBytes, state.Request.MaxTotalImageBytes - bytes);
                if (remaining <= 0) return (null, width, height, "ImageBudgetExceeded");
                using var target = (IDisposable)typeof(IPlatformRenderInterfaceContext).GetMethod("CreateOffscreenRenderTarget")!
                    .Invoke(backend, [new PixelSize(width, height), new Vector(scaling, scaling), true])!;
                using (var canvas = (IDisposable)typeof(IDrawingContextLayerImpl).GetMethod("CreateDrawingContext")!.Invoke(target, null)!)
                {
                    typeof(IDrawingContextImpl).GetMethod("Clear")!.Invoke(canvas, [Colors.Transparent]);
                    typeof(IDrawingContextImpl).GetProperty("Transform")!.SetValue(canvas,
                        Matrix.CreateTranslation(-bounds.X, -bounds.Y) * Matrix.CreateScale(scaling, scaling));
                    // Preserve inherited text/render options, while deliberately excluding transforms,
                    // opacity, clips, cache and effects from these raw own-drawing images.
                    var ancestors = new Stack<object>();
                    object? ancestor = node;
                    for (var i = 0; ancestor is not null && i < 1024; i++, ancestor = Read(ancestor, "Parent"))
                        ancestors.Push(ancestor);
                    foreach (var owner in ancestors)
                    {
                        if (Read(owner, "RenderOptions") is { } options)
                            typeof(IDrawingContextImpl).GetMethod("PushRenderOptions")!.Invoke(canvas, [options]);
                        if (Read(owner, "TextOptions") is { } textOptions)
                            typeof(IDrawingContextImpl).GetMethod("PushTextOptions")!.Invoke(canvas, [textOptions]);
                    }
                    var context = Activator.CreateInstance(RenderContextType, Members, null, [canvas], null)!;
                    var clip = Activator.CreateInstance(typeof(LtrbRect), Members, null, [bounds], null)!;
                    try { RenderCore.Invoke(node, [context, clip]); }
                    catch (TargetInvocationException error)
                    {
                        throw new RpcException("capture_failed", "A compositor node's own drawing failed: " +
                            (error.InnerException?.GetType().Name ?? error.GetType().Name));
                    }
                    foreach (var owner in ancestors.Reverse())
                    {
                        if (Read(owner, "TextOptions") is not null)
                            typeof(IDrawingContextImpl).GetMethod("PopTextOptions")!.Invoke(canvas, null);
                        if (Read(owner, "RenderOptions") is not null)
                            typeof(IDrawingContextImpl).GetMethod("PopRenderOptions")!.Invoke(canvas, null);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                using var output = new BoundedImageStream(remaining);
                try { typeof(IBitmapImpl).GetMethod("Save")!.Invoke(target, [output, new PngBitmapEncoderOptions()]); }
                catch (TargetInvocationException error) when (error.InnerException is ImageBudgetException)
                { return (null, width, height, "ImageBudgetExceeded"); }
                bytes += (int)output.Length;
                return (Convert.ToBase64String(output.GetBuffer(), 0, (int)output.Length), width, height, "Captured");
            }
        }
    }

    private static CompositionClip? DescribeClip(Visual visual)
    {
        var geometry = visual.Clip;
        if (geometry is null) return null;
        var bounds = geometry.Bounds;
        // Arbitrary backend geometries cannot be reliably converted through ToString().
        return geometry switch
        {
            RectangleGeometry rectangle when rectangle.Transform is null || rectangle.Transform.Value.IsIdentity =>
                new CompositionClip("Rectangle", RectDto(rectangle.Rect)),
            EllipseGeometry ellipse when ellipse.Transform is null || ellipse.Transform.Value.IsIdentity =>
                new CompositionClip("Ellipse", RectDto(ellipse.Rect)),
            PathGeometry path when path.GetType() == typeof(PathGeometry) &&
                (path.Transform is null || path.Transform.Value.IsIdentity) &&
                path.Figures is { Count: <= 128 } figures && figures.Sum(figure => figure.Segments?.Count ?? 0) <= 1024 =>
                PathClip(path, bounds),
            _ => new CompositionClip("UnavailablePath", RectDto(bounds))
        };
    }

    private static CompositionClip PathClip(PathGeometry geometry, Rect bounds)
    {
        var path = geometry.ToString();
        if (path.Length > 8192) return new CompositionClip("UnavailablePath", RectDto(bounds));
        if (path.StartsWith("F1 ", StringComparison.Ordinal)) path = path[3..];
        return new CompositionClip(geometry.FillRule == FillRule.NonZero ? "PathNonZero" : "PathEvenOdd", RectDto(bounds), path);
    }

    private static object? Read(object target, string name)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            if (type.GetProperty(name, Members | BindingFlags.DeclaredOnly) is { } property)
                return property.GetValue(target);
            if (type.GetField(name, Members | BindingFlags.DeclaredOnly) is { } field)
                return field.GetValue(target);
        }
        return null;
    }

    private static object? Call(object target, string name) => target.GetType().GetMethod(name, Members)?.Invoke(target, null);
    private static double Number(object target, string name) => Convert.ToDouble(Read(target, name) ??
        throw new RpcException("adapter_unavailable", "The Avalonia composition adapter is missing " + name + "."),
        System.Globalization.CultureInfo.InvariantCulture);
    private static Type RequiredType(string name) => BaseAssembly.GetType(name, true)!;
    private static string? Text(string? text) => text?.Length > 256 ? text[..256] : text;
    private static CompositionRect RectDto(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
    private static CompositionTransform Transform(Matrix matrix) => new(matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32);
    private static bool Finite(params double[] values) => values.All(double.IsFinite);
    private static bool Affine(Matrix matrix) => Finite(matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.M31, matrix.M32) &&
        matrix.M13 == 0 && matrix.M23 == 0 && matrix.M33 == 1;

    private sealed record UiInfo(long ObjectId, string? TypeName, string? Name, int ZIndex, CompositionClip? Clip);
    private sealed class CaptureState(CompositionSnapshotRequest request, object root, long rootObjectId)
    {
        public object Gate { get; } = new();
        public CompositionSnapshotRequest Request { get; } = request;
        public long RootObjectId { get; } = rootObjectId;
        public object? Root { get; private set; } = root;
        public Dictionary<object, UiInfo> Objects { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<object> Excluded { get; } = new(ReferenceEqualityComparer.Instance);
        public void Clear()
        {
            lock (Gate) { Root = null; Objects.Clear(); Excluded.Clear(); }
        }
    }
    private sealed class ImageBudgetException : IOException;
    private sealed class BoundedImageStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > limit) throw new ImageBudgetException();
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > limit) throw new ImageBudgetException();
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            if (Position + 1 > limit) throw new ImageBudgetException();
            base.WriteByte(value);
        }
    }
}
