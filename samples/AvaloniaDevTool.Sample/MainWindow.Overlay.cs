using Avalonia.Interactivity;
using Avalonia.Media;

namespace AvaloniaDevTool.Sample;

public sealed partial class MainWindow
{
    private void ToggleBoxModelTransformClick(object? sender, RoutedEventArgs args)
    {
        BoxModelTarget.RenderTransform = BoxModelTarget.RenderTransform is null ? new RotateTransform(12) : null;
        ViewModel.Status = BoxModelTarget.RenderTransform is null ? "盒模型已复位。" : "盒模型旋转 12°，高亮和分区应跟随。";
    }
}
