using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Themes.Simple;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        AppBuilder.Configure<FixtureApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
}

public sealed class FixtureApp : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var count = 0;
            var counter = new TextBlock { Name = "CounterText", Text = "Clicks: 0" };
            var button = new Button { Name = "IncrementButton", Content = "Increment" };
            button.Click += (_, _) => counter.Text = $"Clicks: {++count}";
            desktop.MainWindow = new Window
            {
                Title = "Uninstrumented Avalonia fixture", Width = 460, Height = 240,
                Content = new StackPanel
                {
                    Margin = new Thickness(24), Spacing = 12, Orientation = Orientation.Vertical,
                    Children =
                    {
                        new TextBlock { Name = "StatusText", Text = "No Agent reference or startup hook" },
                        counter, button
                    }
                }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
