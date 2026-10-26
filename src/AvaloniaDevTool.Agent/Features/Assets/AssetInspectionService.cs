using System.Text;
using System.Text.Json;
using Avalonia.Platform;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class AssetInspectionService
{
    internal const int PreviewLimit = 1024 * 1024;
    internal const long MaximumImagePixels = 16 * 1024 * 1024;

    public static IReadOnlyList<AssetInfo> List()
    {
        var assets = new List<AssetInfo>();
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
        {
            if (!assembly.GetManifestResourceNames().Contains("!AvaloniaResources"))
                continue;
            var name = assembly.GetName().Name;
            if (name is null)
                continue;
            try
            {
                foreach (var uri in AssetLoader.GetAssets(new Uri("avares://" + name + "/"), null))
                {
                    assets.Add(new AssetInfo(uri.AbsoluteUri,
                        name,
                        Path.GetExtension(uri.AbsolutePath).ToLowerInvariant()));
                    if (assets.Count >= 2000)
                        return assets;
                }
            }
            catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException
                                                  or ArgumentException)
            {
            }
        }

        return assets.OrderBy(asset => asset.Uri, StringComparer.Ordinal).ToArray();
    }

    internal static Uri ValidateUri(string uriText)
    {
        if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || uri.Scheme != "avares" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 || !uri.IsDefaultPort)
            throw new RpcException("unsupported_uri", "Only avares assets from the target application can be opened.");
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => !value.IsDynamic &&
            string.Equals(value.GetName().Name, uri.Host, StringComparison.OrdinalIgnoreCase));
        if (assembly is null || !assembly.GetManifestResourceNames().Contains("!AvaloniaResources") ||
            !AssetLoader.Exists(uri))
            throw new RpcException("asset_not_found", "The asset is not embedded in a loaded target assembly.");
        return uri;
    }

    internal static long? TryGetLength(Stream stream)
    {
        try
        {
            return stream.CanSeek ? stream.Length : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static AssetPreview Open(string uriText)
    {
        var uri = ValidateUri(uriText);
        try
        {
            using var stream = AssetLoader.Open(uri);
            return ReadPreview(uri, stream);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            throw new RpcException("asset_read_failed", ex.Message);
        }
    }

    internal static AssetPreview ReadPreview(Uri uri, Stream stream)
    {
        var total = TryGetLength(stream);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (bytes.Length <= PreviewLimit)
        {
            var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, PreviewLimit + 1L - bytes.Length));
            if (read == 0)
                break;
            bytes.Write(buffer, 0, read);
        }

        var data = bytes.ToArray();
        var truncated = data.Length > PreviewLimit;
        if (!truncated)
            total = data.Length;
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        var mediaType = extension switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".webp" => "image/webp",
            ".gif" => "image/gif", ".bmp" => "image/bmp", ".svg" => "image/svg+xml", ".json" => "application/json",
            ".axaml" or ".xaml" or ".xml" => "application/xml", ".txt" or ".md" or ".css" or ".csv" => "text/plain",
            ".ttf" => "font/ttf", ".otf" => "font/otf", ".woff" => "font/woff", ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
        var isText = mediaType is "image/svg+xml" or "application/json" or "application/xml" or "text/plain";
        var body = truncated ? data.AsSpan(0, PreviewLimit).ToArray() : data;
        var image = isText ? null : AssetMetadataReader.ReadImage(body);
        if (image is { Width: <= 0 } or { Height: <= 0 })
            image = null;
        var font = mediaType.StartsWith("font/", StringComparison.Ordinal) ? AssetMetadataReader.ReadFont(body) : null;
        string? notice = null;
        var canDecodeImage = image is { Width: > 0 and <= 32768, Height: > 0 and <= 32768 } &&
                             (long)image.Width * image.Height <= MaximumImagePixels;
        if (mediaType.StartsWith("image/", StringComparison.Ordinal) && !isText && !canDecodeImage)
            notice = image is null ? "无法识别图像头，未解码预览。" : "图像超过 16 Mi 像素或 32768 边长，未解码预览。";
        if (mediaType.StartsWith("font/", StringComparison.Ordinal) && font?.FamilyName is null)
            notice = "字体格式无效或暂不支持（仅预览完整 TTF/OTF）。";
        string? text = null;
        if (isText)
        {
            var chars = new char[Encoding.UTF8.GetMaxCharCount(body.Length)];
            Encoding.UTF8.GetDecoder().Convert(body, chars, !truncated, out _, out var used, out _);
            text = new string(chars, 0, used);
            var jsonBytes = ProtocolJson.SerializeToUtf8Bytes(text).Length;
            const int textJsonBudget = 3 * 1024 * 1024;
            if (jsonBytes > textJsonBudget)
            {
                var keep = Math.Min(text.Length, textJsonBudget / 6 - 8);
                if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
                    keep--;
                text = text[..keep];
                truncated = true;
                notice = "文本 JSON 转义后达到帧大小预算，显示内容进一步截断；完整导出不受预览限制。";
            }
        }

        var binaryPreview = !truncated && (canDecodeImage || font?.FamilyName is not null);
        return new AssetPreview(uri.AbsoluteUri,
            mediaType,
            text,
            binaryPreview ? Convert.ToBase64String(body) : null,
            body.Length,
            truncated,
            total,
            image,
            font,
            notice);
    }
}
