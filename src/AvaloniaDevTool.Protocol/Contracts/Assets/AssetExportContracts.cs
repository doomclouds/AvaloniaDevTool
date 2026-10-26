namespace AvaloniaDevTool.Protocol;

public static class AssetExportMethods
{
    public const string Begin = "assets.export.begin";
    public const string Read = "assets.export.read";
    public const string Close = "assets.export.close";
    public const int ChunkBytes = 64 * 1024;
    public const long MaximumBytes = 100L * 1024 * 1024;
}

public sealed record BeginAssetExportRequest(string Uri);

public sealed record BeginAssetExportResult(
    string Token,
    string Uri,
    long? TotalBytes,
    int ChunkBytes,
    long MaximumBytes);

public sealed record ReadAssetExportRequest(string Token, long Offset);

public sealed record AssetExportChunk(long Offset, byte[] Bytes, bool IsComplete, long? TotalBytes, string? Sha256);

public sealed record CloseAssetExportRequest(string Token);

public sealed record AssetExportResult(string Uri, long ByteLength, string Sha256);

public sealed record AssetExportProgress(long BytesWritten, long? TotalBytes);

public sealed record AssetImageMetadata(string Format, int Width, int Height, double? DpiX = null, double? DpiY = null)
{
    public long? DecodedPixelBytes => Width > 0 && Height > 0 && (long)Width * Height <= long.MaxValue / 4
        ? (long)Width * Height * 4
        : null;
}

public sealed record AssetFontMetadata(string Format, string? FamilyName);
