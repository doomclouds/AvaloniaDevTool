using Avalonia.Media;
using Avalonia.Media.Fonts;
using Avalonia.Media.Imaging;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _assetExportCancellation;
    private PreviewFontCollection? _assetFontCollection;
    private FontFamily? _assetFontFamily;
    private string _assetMetadata = "";
    private string _assetExportStatus = "选择资产可复制 URI 或导出原始文件。";
    private double _assetExportProgress;
    private bool _isExportingAsset;
    public AsyncCommand CancelAssetExportCommand { get; private set; } = null!;
    public string AssetMetadata { get => _assetMetadata; private set => Set(ref _assetMetadata, value); }
    public string AssetExportStatus { get => _assetExportStatus; private set => Set(ref _assetExportStatus, value); }
    public double AssetExportProgress { get => _assetExportProgress; private set => Set(ref _assetExportProgress, value); }
    public bool IsExportingAsset { get => _isExportingAsset; private set { if (Set(ref _isExportingAsset, value)) RefreshAssetCommands(); } }
    public bool CanExportAsset => CanUseNativeTools && SelectedAsset is not null && !IsExportingAsset;
    public bool CanCopyAssetUri => SelectedAsset is not null;
    public bool HasAssetFont => AssetFontFamily is not null;
    public FontFamily? AssetFontFamily { get => _assetFontFamily; private set { if (Set(ref _assetFontFamily, value)) Raise(nameof(HasAssetFont)); } }
    public string AssetFontSample => "The quick brown fox jumps over the lazy dog.\nABCDEFGHIJKLMNOPQRSTUVWXYZ\nabcdefghijklmnopqrstuvwxyz 0123456789\n字体预览 · Avalonia 内嵌资产";

    private void InitializeAssets() => CancelAssetExportCommand = new AsyncCommand(() =>
    { CancelAssetExport(); return Task.CompletedTask; }, ReportError, () => IsExportingAsset);
    private void RefreshAssetCommands()
    {
        Raise(nameof(CanExportAsset));
        Raise(nameof(CanCopyAssetUri));
        CancelAssetExportCommand?.Refresh();
    }
    public string? GetSelectedAssetUri() => SelectedAsset?.Uri;
    public void ReportAssetAction(string text) => AssetExportStatus = text;
    public void CancelAssetExport() => _assetExportCancellation?.Cancel();

    public Task<AssetExportResult?> ExportSelectedAssetToFileAsync(string path, bool overwrite = false) =>
        ExportSelectedAssetCoreAsync((uri, progress, token) => _client.ExportAssetToFileAsync(uri, path, overwrite, progress, token));

    /// <summary>Exports into a caller-owned stream, also used by non-file destinations and tests.</summary>
    public Task<AssetExportResult?> ExportSelectedAssetAsync(Stream destination) =>
        ExportSelectedAssetCoreAsync((uri, progress, token) => _client.ExportAssetAsync(uri, destination, progress, token));

    private async Task<AssetExportResult?> ExportSelectedAssetCoreAsync(
        Func<string, IProgress<AssetExportProgress>, CancellationToken, Task<AssetExportResult>> export)
    {
        if (!CanExportAsset || SelectedAsset is not { } asset) return null;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _assetExportCancellation = cancellation;
        IsExportingAsset = true;
        AssetExportProgress = 0;
        AssetExportStatus = "正在导出原始文件…";
        try
        {
            var progress = new Progress<AssetExportProgress>(value =>
            {
                if (!ReferenceEquals(_assetExportCancellation, cancellation)) return;
                AssetExportProgress = value.TotalBytes is > 0 ? 100d * value.BytesWritten / value.TotalBytes.Value : 0;
                AssetExportStatus = value.TotalBytes is { } total ? $"导出 {value.BytesWritten:N0} / {total:N0} 字节" : $"导出 {value.BytesWritten:N0} 字节";
            });
            var result = await export(asset.Uri, progress, cancellation.Token);
            AssetExportProgress = 100;
            AssetExportStatus = $"已导出 {result.ByteLength:N0} 字节 · SHA-256 {result.Sha256}";
            return result;
        }
        catch (OperationCanceledException) { AssetExportStatus = "已取消导出。"; return null; }
        catch (Exception ex) { AssetExportStatus = $"导出失败：{ex.Message}"; return null; }
        finally { _assetExportCancellation = null; IsExportingAsset = false; }
    }

    private void ApplyAssetPreview(AssetPreview preview)
    {
        var text = preview.Text ?? "";
        const int displayedTextCharacters = 64 * 1024;
        var textCut = text.Length > displayedTextCharacters;
        var keep = textCut ? displayedTextCharacters : text.Length;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
        AssetText = text[..keep];
        var details = new List<string>
        {
            $"程序集：{SelectedAsset?.AssemblyName}",
            $"URI：{preview.Uri}",
            $"类型：{preview.MediaType}",
            preview.TotalByteLength is { } total ? $"原始大小：{total:N0} 字节" : "原始大小：未知（流未提供长度）",
            $"已读取预览：{preview.ByteLength:N0} 字节" + (preview.IsTruncated ? "（已截断）" : "")
        };
        if (preview.Image is { } image)
        {
            details.Add($"图像：{image.Format} · {image.Width:N0} × {image.Height:N0} 像素");
            details.Add(image.DpiX is { } dx && image.DpiY is { } dy ? $"DPI：{dx:0.##} × {dy:0.##}" : "DPI：文件未声明");
            details.Add(image.DecodedPixelBytes is { } decoded ? $"RGBA 解码估算：{decoded:N0} 字节（不含解码器/绘制缓存）" : "RGBA 解码估算：无效尺寸，无法计算");
        }
        if (preview.Font is { } font) details.Add($"字体：{font.Format} · {font.FamilyName ?? "无有效家族名"}");
        if (textCut) details.Add("文本控件显示前 64 Ki 字符，以限制排版内存；完整导出保留全部字节。");
        if (preview.Base64 is { Length: > 0 } base64)
        {
            using var bytes = new MemoryStream(Convert.FromBase64String(base64));
            try
            {
                if (preview.Font?.FamilyName is not null)
                {
                    var collection = new PreviewFontCollection();
                    try
                    {
                        if (!collection.TryAddGlyphTypeface(bytes, out var glyph))
                        {
                            glyph?.Dispose();
                            throw new InvalidDataException("字体解码器无法读取此字体。");
                        }
                        var family = new FontFamily(collection.Key.AbsoluteUri + "#" + glyph.FamilyName);
                        FontManager.Current.AddFontCollection(collection);
                        _assetFontCollection = collection;
                        AssetFontFamily = family;
                        details.Add($"实际预览家族：{glyph.FamilyName}");
                    }
                    finally { if (!ReferenceEquals(collection, _assetFontCollection)) ((IDisposable)collection).Dispose(); }
                }
                else if (preview.Image is { Width: > 0 and <= 32768, Height: > 0 and <= 32768 } metadata &&
                         (long)metadata.Width * metadata.Height <= 16 * 1024 * 1024)
                    AssetImage = new Bitmap(bytes);
            }
            catch (Exception ex) { details.Add($"预览不可用：{ex.Message}"); }
        }
        if (preview.PreviewNotice is { } notice) details.Add(notice);
        if (preview.IsTruncated) details.Add("仅显示文件前 1 MiB；导出读取完整原始内容。大图/字体截断后不解码。");
        if (preview.MediaType == "image/svg+xml") details.Add("SVG 以源码显示。");
        AssetMetadata = string.Join("\n", details);
        AssetSummary = $"{preview.MediaType} · " + (preview.TotalByteLength is { } length ? $"{length:N0} 字节" : "大小未知") +
            (preview.IsTruncated ? " · 预览已截断" : "");
        Raise(nameof(HasAssetImage));
        Raise(nameof(HasAssetText));
    }

    private void ClearAssetMetadata()
    {
        AssetMetadata = "";
        AssetFontFamily = null;
        if (_assetFontCollection is { } font)
        {
            _assetFontCollection = null;
            FontManager.Current.RemoveFontCollection(font.Key);
        }
    }
    private sealed class PreviewFontCollection : FontCollectionBase
    { public override Uri Key { get; } = new("fonts://asset-preview/" + Guid.NewGuid().ToString("N")); }
}
