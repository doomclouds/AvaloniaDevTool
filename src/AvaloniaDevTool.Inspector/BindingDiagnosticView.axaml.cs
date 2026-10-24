using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class BindingDiagnosticView : UserControl
{
    public BindingDiagnosticView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RebuildCard();
        AttachedToVisualTree += (_, _) => RebuildCard();
    }

    private void RebuildCard()
    {
        var owner = this.GetVisualAncestors().OfType<StylesView>().FirstOrDefault()?.DataContext as MainWindowViewModel;
        BindingContent.DataContext = DataContext is BindingDiagnosticInfo binding
            ? owner?.CreateBindingDiagnosticCard(binding) ?? new BindingDiagnosticCardViewModel(binding) : null;
        if (BindingContent.DataContext is BindingDiagnosticCardViewModel { IsNavigationTarget: true } && TopLevel.GetTopLevel(this) is not null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.BringIntoView());
    }
}
