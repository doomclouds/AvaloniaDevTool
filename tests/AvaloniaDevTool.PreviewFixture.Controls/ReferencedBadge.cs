using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace AvaloniaDevTool.PreviewFixture.Controls;

public sealed class ReferencedBadge : Border
{
    public ReferencedBadge() => Child = new TextBlock { Name = "ReferencedControlText", Text = "Referenced assembly control" };
}

public sealed partial class ReferencedView : UserControl
{
    public ReferencedView() => AvaloniaXamlLoader.Load(this);
}
