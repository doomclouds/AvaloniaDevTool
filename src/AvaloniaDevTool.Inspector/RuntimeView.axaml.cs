using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AvaloniaDevTool.Inspector;

public sealed partial class RuntimeView : UserControl
{
    public RuntimeView() => InitializeComponent();

    private void OpenRuntimeValue(object? sender, TappedEventArgs args)
    {
        if (args.Source is not Control source) return;
        var row = source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as RuntimeRowViewModel;
        if (row?.OpenCommand.CanExecute(null) == true)
        {
            row.OpenCommand.Execute(null);
            args.Handled = true;
        }
    }
}
