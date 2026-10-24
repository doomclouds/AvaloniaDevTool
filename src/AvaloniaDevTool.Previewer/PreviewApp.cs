using Avalonia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace AvaloniaDevTool.Previewer;

public sealed class PreviewApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }
}

