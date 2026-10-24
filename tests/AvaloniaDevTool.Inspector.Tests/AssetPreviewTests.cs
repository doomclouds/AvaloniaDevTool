using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Fonts.Inter;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class AssetPreviewTests
{
    [AvaloniaFact]
    public async Task Asset_view_copies_uri_cancels_picker_and_exports_full_file_with_visible_status()
    {
        var target = new Window(); target.Show();
        var host = new Window { Width = 1200, Height = 800 };
        try
        {
            await using var agent = Attach(target);
            await using var model = new MainWindowViewModel(agent.PipeName);
            await model.InitializeAsync(); model.InspectionTabIndex = 3;
            await model.RefreshActiveInspectionAsync();
            var view = new AssetsView { DataContext = model }; host.Content = view; host.Show();
            var exportButton = Find<Button>(view, "Inspector.ExportAsset");
            Assert.False(exportButton.IsEnabled);
            model.SelectedAsset = model.FilteredAssets.Single(asset => asset.Uri.Contains("AvaloniaDevTool.Inspector.Tests/Assets/asset-long-utf8.txt", StringComparison.OrdinalIgnoreCase));
            await WaitUntilAsync(() => model.AssetText.Length > 0);
            Assert.True(exportButton.IsEnabled); Assert.Contains("预览已截断", model.AssetSummary);
            Assert.Contains("原始大小", Find<TextBlock>(view, "Inspector.AssetMetadata").Text);
            await view.CopySelectedUriAsync();
            Assert.Equal(model.SelectedAsset.Uri, await host.Clipboard!.TryGetTextAsync());
            var requested = false;
            await view.ExportSelectedWithPickerAsync(options =>
            {
                requested = true; Assert.True(options.ShowOverwritePrompt); Assert.Equal("asset-long-utf8.txt", options.SuggestedFileName);
                return Task.FromResult<Avalonia.Platform.Storage.IStorageFile?>(null);
            });
            Assert.True(requested); Assert.Contains("已取消选择", model.AssetExportStatus); Assert.False(model.IsExportingAsset);
            using var slow = new SlowDestination();
            var canceled = model.ExportSelectedAssetAsync(slow);
            await WaitUntilAsync(() => model.IsExportingAsset && slow.Length > 0);
            Assert.True(Find<ProgressBar>(view, "Inspector.AssetExportProgress").IsEffectivelyVisible);
            Assert.False(exportButton.IsEnabled);
            Assert.True(Find<Button>(view, "Inspector.CancelAssetExport").IsEnabled);
            model.CancelAssetExportCommand.Execute(null);
            Assert.Null(await canceled); Assert.Contains("已取消导出", model.AssetExportStatus);
            using var destination = new MemoryStream();
            var result = await model.ExportSelectedAssetAsync(destination);
            Assert.NotNull(result); Assert.True(result.ByteLength > 1024 * 1024);
            Assert.Equal(result.Sha256, Convert.ToHexString(SHA256.HashData(destination.ToArray())).ToLowerInvariant());
            Assert.Equal(100, model.AssetExportProgress); Assert.Contains(result.Sha256, model.AssetExportStatus);
            Assert.Contains("已导出", Find<TextBlock>(view, "Inspector.AssetExportStatus").Text);
        }
        finally { host.Close(); target.Close(); }
    }

    [AvaloniaFact]
    public async Task Font_preview_renders_actual_asset_family_and_releases_registration_when_cleared()
    {
        _ = typeof(InterFontCollection).Assembly;
        var target = new Window(); target.Show();
        var host = new Window { Width = 1100, Height = 800 };
        try
        {
            await using var agent = Attach(target);
            await using var model = new MainWindowViewModel(agent.PipeName);
            await model.InitializeAsync(); model.InspectionTabIndex = 3; await model.RefreshActiveInspectionAsync();
            var view = new AssetsView { DataContext = model }; host.Content = view; host.Show();
            model.SelectedAsset = model.FilteredAssets.First(asset => asset.AssemblyName == "Avalonia.Fonts.Inter" && asset.Extension == ".ttf");
            await WaitUntilAsync(() => model.HasAssetFont || model.AssetMetadata.Contains("预览不可用", StringComparison.Ordinal));
            Assert.True(model.HasAssetFont, model.AssetMetadata);
            var font = model.AssetFontFamily!;
            Assert.True(FontManager.Current.TryGetGlyphTypeface(new Typeface(font), out var glyph));
            Assert.Contains("Inter", glyph.FamilyName);
            host.UpdateLayout();
            Assert.Same(font, Find<TextBlock>(view, "Inspector.AssetFontPreview").FontFamily);
            Assert.True(Find<TextBlock>(view, "Inspector.AssetFontPreview").IsEffectivelyVisible);
            Assert.False(model.HasAssetText); Assert.Contains("实际预览家族", model.AssetMetadata);
            model.SelectedAsset = null;
            Assert.False(model.HasAssetFont);
            Assert.False(FontManager.Current.TryGetGlyphTypeface(new Typeface(font), out _));
            model.SelectedAsset = model.FilteredAssets.Single(asset => asset.Uri.Contains("AvaloniaDevTool.Inspector.Tests/Assets/invalid.ttf", StringComparison.OrdinalIgnoreCase));
            await WaitUntilAsync(() => model.AssetMetadata.Contains("字体格式无效", StringComparison.Ordinal));
            Assert.False(model.HasAssetFont); Assert.False(model.HasAssetText);
            model.SelectedAsset = model.FilteredAssets.Single(asset => asset.Uri.Contains("AvaloniaDevTool.Inspector.Tests/Assets/pixel.png", StringComparison.OrdinalIgnoreCase));
            await WaitUntilAsync(() => model.HasAssetImage || model.AssetMetadata.Contains("预览不可用", StringComparison.Ordinal));
            Assert.True(model.HasAssetImage, model.AssetMetadata);
            Assert.Equal(1, model.AssetImage!.PixelSize.Width); Assert.Equal(1, model.AssetImage.PixelSize.Height);
            Assert.Contains("RGBA 解码估算：4 字节", model.AssetMetadata);
        }
        finally { host.Close(); target.Close(); }
    }

    private static DiagnosticsAgent Attach(Window target) => DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
    { PipeName = "avdt-assets-ui-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [target] });
    private static T Find<T>(Control view, string id) where T : Control => view.GetVisualDescendants().OfType<T>()
        .Single(control => AutomationProperties.GetAutomationId(control) == id);
    private static async Task WaitUntilAsync(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)); while (!predicate()) await Task.Delay(15, timeout.Token); }
    private sealed class SlowDestination : MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(30, cancellationToken); await base.WriteAsync(buffer, cancellationToken); }
    }
}
