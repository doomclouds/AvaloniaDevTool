using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;

namespace AvaloniaDevTool.Sample;

public sealed partial class MainWindow
{
    private async void CopyRemoteTokenClick(object? sender, RoutedEventArgs args)
    {
        if (App.RemoteToken is not { } token || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            ViewModel.Status = "请先使用 --remote 启动此应用的回环端点。";
            return;
        }

        try
        {
            await clipboard.SetTextAsync(token);
            ViewModel.Status = "本次连接令牌已复制；请作为诊断客户端的独立连接参数使用，令牌不放入地址。";
        }
        catch (Exception error)
        {
            ViewModel.Status = "复制令牌失败：" + error.GetType().Name;
        }
    }
}
