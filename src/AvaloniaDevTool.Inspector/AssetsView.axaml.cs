using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace AvaloniaDevTool.Inspector;

public sealed partial class AssetsView : UserControl
{
    public AssetsView() => InitializeComponent();

    public async Task CopySelectedUriAsync()
    {
        if (DataContext is not MainWindowViewModel model || model.GetSelectedAssetUri() is not { } uri) return;
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) { model.ReportAssetAction("当前窗口没有可用剪贴板。"); return; }
            await clipboard.SetTextAsync(uri);
            model.ReportAssetAction("已复制资产 URI。");
        }
        catch (Exception ex) { model.ReportAssetAction($"复制失败：{ex.Message}"); }
    }

    public async Task ExportSelectedWithPickerAsync(Func<FilePickerSaveOptions, Task<IStorageFile?>>? picker = null)
    {
        if (DataContext is not MainWindowViewModel model || !model.CanExportAsset || model.GetSelectedAssetUri() is not { } uri) return;
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (picker is null && storage?.CanSave != true) { model.ReportAssetAction("当前窗口不支持保存文件。"); return; }
            picker ??= options => storage!.SaveFilePickerAsync(options);
            var file = await picker(new FilePickerSaveOptions
            {
                Title = "导出内嵌资产原始文件", SuggestedFileName = Path.GetFileName(new Uri(uri).LocalPath),
                ShowOverwritePrompt = true
            });
            if (file is null) { model.ReportAssetAction("已取消选择导出位置。"); return; }
            using (file)
            {
                if (model.GetSelectedAssetUri() != uri) { model.ReportAssetAction("选择的资产已改变，请重新选择导出位置。"); return; }
                if (file.TryGetLocalPath() is not { } path) { model.ReportAssetAction("请选择本地文件位置。"); return; }
                await model.ExportSelectedAssetToFileAsync(path, overwrite: true);
            }
        }
        catch (Exception ex) { model.ReportAssetAction($"导出失败：{ex.Message}"); }
    }
    private async void CopyUriClick(object? sender, RoutedEventArgs args) => await CopySelectedUriAsync();
    private async void ExportClick(object? sender, RoutedEventArgs args) => await ExportSelectedWithPickerAsync();
}

