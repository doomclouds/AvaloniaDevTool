using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AvaloniaDevTool.Inspector;

public sealed partial class EventRoutesView : UserControl
{
    public EventRoutesView() => AvaloniaXamlLoader.Load(this);
}
