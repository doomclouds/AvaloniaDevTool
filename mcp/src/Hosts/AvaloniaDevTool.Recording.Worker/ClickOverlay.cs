using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ScreenRecorderLib;

namespace AvaloniaDevTool.Recording.Worker;

/// <summary>GPU-composited click images; no target window or synthetic mouse input is created.</summary>
internal sealed class ClickOverlay
{
    private const int Diameter = 56;
    private readonly ImageOverlay _left;
    private readonly ImageOverlay _right;
    private readonly ImageOverlay _pointer;
    private long _lastMarker;
    private long _lastPointerMarker;
    private bool _pressed;
    private bool _rightPressed;

    internal ClickOverlay()
    {
        _left = Create("left", 45, 158, 255);
        _right = Create("right", 255, 153, 45);
        _pointer = Create("agent-pointer", 0, 0, 0, arrow: true);
    }

    internal List<RecordingOverlayBase> Images => [_left, _right, _pointer];

    internal void Update(Recorder recorder, double x, double y, string kind, string? button, long timestamp)
    {
        var normalized = kind.Replace("-", "").Replace("_", "").ToLowerInvariant();
        if (normalized is not ("pointerdown" or "pointermove" or "pointerup" or "click" or "doubleclick" or "wheel" or "scroll"))
            return;
        // The arrow hotspot is the first opaque pixel, rather than its image centre.
        _pointer.Offset = new ScreenSize(Math.Round(x - 1), Math.Round(y - 1));
        recorder.GetDynamicOptionsBuilder().SetUpdatedOverlay(_pointer).Apply();
        _lastPointerMarker = timestamp;
        var right = string.Equals(button, "right", StringComparison.OrdinalIgnoreCase);
        switch (normalized)
        {
            case "pointerdown":
                _pressed = true;
                _rightPressed = right;
                break;
            case "pointermove":
                if (!_pressed) return;
                right = _rightPressed;
                break;
            case "pointerup":
                _pressed = false;
                break;
            case "click":
            case "doubleclick":
                _pressed = false;
                break;
            case "wheel":
            case "scroll":
                right = true;
                break;
            default:
                return;
        }
        Hide(recorder);
        var overlay = right ? _right : _left;
        overlay.Offset = new ScreenSize(Math.Round(x - Diameter / 2.0), Math.Round(y - Diameter / 2.0));
        recorder.GetDynamicOptionsBuilder().SetUpdatedOverlay(overlay).Apply();
        _lastMarker = timestamp;
    }

    internal void HideExpired(Recorder recorder, long timestamp, long frequency)
    {
        if (!_pressed && _lastMarker != 0 && (timestamp - _lastMarker) / (double)frequency >= 0.4)
            Hide(recorder);
        if (!_pressed && _lastPointerMarker != 0 && (timestamp - _lastPointerMarker) / (double)frequency >= 0.8)
            HidePointer(recorder);
    }

    internal void UpdateOutside(Recorder recorder, string kind)
    {
        var normalized = kind.Replace("-", "").Replace("_", "").ToLowerInvariant();
        if (normalized is "pointerup" or "pointerdown") _pressed = false;
        if (normalized is "pointerup" or "pointerdown" or "pointermove") Hide(recorder);
        HidePointer(recorder);
    }

    private void Hide(Recorder recorder)
    {
        _left.Offset = new ScreenSize(-4096, -4096);
        _right.Offset = new ScreenSize(-4096, -4096);
        recorder.GetDynamicOptionsBuilder().SetUpdatedOverlay(_left).SetUpdatedOverlay(_right).Apply();
        _lastMarker = 0;
    }

    private void HidePointer(Recorder recorder)
    {
        _pointer.Offset = new ScreenSize(-4096, -4096);
        recorder.GetDynamicOptionsBuilder().SetUpdatedOverlay(_pointer).Apply();
        _lastPointerMarker = 0;
    }

    private static ImageOverlay Create(string name, byte red, byte green, byte blue, bool arrow = false)
    {
        var width = arrow ? 24 : Diameter;
        var height = arrow ? 32 : Diameter;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AvaloniaDevTool", "recording-assets");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "click-v1-" + name + ".png");
        if (!File.Exists(path))
        {
            using var data = new MemoryStream();
            for (var y = 0; y < height; y++)
            {
                data.WriteByte(0);
                for (var x = 0; x < width; x++)
                {
                    if (arrow)
                    {
                        var inside = InsideArrow(x, y);
                        var edge = !InsideArrow(x - 1, y) || !InsideArrow(x + 1, y) ||
                            !InsideArrow(x, y - 1) || !InsideArrow(x, y + 1);
                        var color = edge ? (byte)0 : (byte)255;
                        data.WriteByte(color);
                        data.WriteByte(color);
                        data.WriteByte(color);
                        data.WriteByte(inside ? (byte)255 : (byte)0);
                    }
                    else
                    {
                        var radius = Math.Sqrt(Math.Pow(x - 27.5, 2) + Math.Pow(y - 27.5, 2));
                        data.WriteByte(red);
                        data.WriteByte(green);
                        data.WriteByte(blue);
                        data.WriteByte(radius is >= 20 and <= 25 ? (byte)240 : radius < 20 ? (byte)42 : (byte)0);
                    }
                }
            }
            using var png = new MemoryStream();
            png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8;
            header[9] = 6;
            WriteChunk(png, "IHDR", header);
            using var compressed = new MemoryStream();
            using (var deflate = new ZLibStream(compressed, CompressionLevel.Fastest, true))
                data.WriteTo(deflate);
            WriteChunk(png, "IDAT", compressed.ToArray());
            WriteChunk(png, "IEND", []);
            try
            {
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                png.WriteTo(output);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        return new ImageOverlay(path)
        {
            AnchorPoint = Anchor.TopLeft,
            Offset = new ScreenSize(-4096, -4096),
            Size = new ScreenSize(width, height),
            Stretch = StretchMode.None
        };
    }

    private static bool InsideArrow(int x, int y)
    {
        ReadOnlySpan<(int X, int Y)> points = [(1, 1), (1, 25), (7, 19), (12, 30), (17, 28), (12, 18), (23, 18)];
        var inside = false;
        var px = x + 0.5;
        var py = y + 0.5;
        for (var current = 0; current < points.Length; current++)
        {
            var previous = current == 0 ? points.Length - 1 : current - 1;
            var a = points[current];
            var b = points[previous];
            if ((a.Y > py) != (b.Y > py) && px < (b.X - a.X) * (py - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var name = Encoding.ASCII.GetBytes(type);
        output.Write(name);
        output.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in name.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }
}
