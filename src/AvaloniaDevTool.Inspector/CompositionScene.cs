using System.Buffers.Binary;
using Avalonia.Media.Imaging;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

/// <summary>Owns decoded images for one bounded, immutable compositor capture.</summary>
public sealed class CompositionScene : IDisposable
{
    private readonly Dictionary<long, Bitmap> _images = [];
    private bool _disposed;
    public CompositionSnapshot Snapshot { get; }
    public IReadOnlyDictionary<long, Bitmap> Images => _images;
    public bool IsDisposed => _disposed;

    public CompositionScene(CompositionSnapshot snapshot)
    {
        Snapshot = snapshot;
        if (snapshot.Layers.Count > 256 || snapshot.Layers.Select(layer => layer.CaptureId).Distinct().Count() != snapshot.Layers.Count)
            throw new RpcException("invalid_capture", "The composition layer identities or count are invalid.");
        if (!Finite(snapshot.Width) || !Finite(snapshot.Height) || snapshot.Width <= 0 || snapshot.Height <= 0)
            throw new RpcException("invalid_capture", "The composition root dimensions are invalid.");
        var identities = snapshot.Layers.ToDictionary(layer => layer.CaptureId);
        foreach (var layer in snapshot.Layers)
        {
            if (layer.Depth is < 0 or > 64 || layer.ChildIndex < 0 || !Finite(layer.Width) || !Finite(layer.Height) ||
                layer.Width < 0 || layer.Height < 0 || !Finite(layer.Opacity) || layer.Opacity is < 0 or > 1 ||
                !Valid(layer.Transform) || !Valid(layer.TransformToRoot) ||
                layer.ParentCaptureId is { } parent && (!identities.TryGetValue(parent, out var owner) || owner.Depth + 1 != layer.Depth) ||
                layer.ContentBounds is { } content && (!Finite(content.X) || !Finite(content.Y) || !Finite(content.Width) || !Finite(content.Height) || content.Width < 0 || content.Height < 0))
                throw new RpcException("invalid_capture", "A composition layer contains invalid geometry or hierarchy.");
        }
        long pixels = 0;
        long bytes = 0;
        try
        {
            foreach (var layer in snapshot.Layers)
            {
                if (layer.PngBase64 is not { } encoded) continue;
                if (layer.ImageStatus != "Captured" || layer.PixelWidth is < 1 or > 16384 || layer.PixelHeight is < 1 or > 16384)
                    throw new RpcException("invalid_capture", "An image has invalid capture dimensions or status.");
                pixels += (long)layer.PixelWidth * layer.PixelHeight;
                if (pixels > 4_194_304 || encoded.Length > 2_800_000)
                    throw new RpcException("invalid_capture", "The decoded composition exceeds the image budget.");
                var png = Convert.FromBase64String(encoded);
                bytes += png.Length;
                if (bytes > 2_097_152 || png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                    BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) != layer.PixelWidth ||
                    BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) != layer.PixelHeight)
                    throw new RpcException("invalid_capture", "The composition PNG data is invalid or exceeds the budget.");
                using var stream = new MemoryStream(png, false);
                var image = new Bitmap(stream);
                if (image.PixelSize.Width != layer.PixelWidth || image.PixelSize.Height != layer.PixelHeight)
                {
                    image.Dispose();
                    throw new RpcException("invalid_capture", "The PNG dimensions do not match the composition metadata.");
                }
                _images.Add(layer.CaptureId, image);
            }
        }
        catch { Dispose(); throw; }
    }

    private static bool Finite(double value) => double.IsFinite(value) && Math.Abs(value) <= 10_000_000;
    private static bool Valid(CompositionTransform value) => Finite(value.M11) && Finite(value.M12) && Finite(value.M21) && Finite(value.M22) && Finite(value.OffsetX) && Finite(value.OffsetY);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var image in _images.Values) image.Dispose();
        _images.Clear();
    }
}
