using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;

namespace AvaloniaDevTool.PreviewFixture;

public static class Program
{
    public static void Main() => throw new InvalidOperationException("Application Main must never execute during preview.");
    public static AppBuilder BuildAvaloniaApp()
    {
        if (!Design.IsDesignMode) throw new InvalidOperationException("The builder requires the real designer mode flag.");
        Console.WriteLine("Target BuildAvaloniaApp stdout is deliberately noisy.");
        return AppBuilder.Configure<FixtureApp>().UsePlatformDetect();
    }
}

public sealed class FixtureApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources["FixtureTitle"] = "Project App resource";
        Resources["FixtureReferencedType"] = typeof(Controls.ReferencedBadge);
    }
}

public sealed partial class FixtureView : UserControl
{
    public FixtureView() => AvaloniaXamlLoader.Load(this);
}

public sealed partial class FixtureWindow : Window
{
    public FixtureWindow() => AvaloniaXamlLoader.Load(this);
}

public sealed class FixtureBadge : Border
{
    public FixtureBadge()
    {
        if (!Design.IsDesignMode) throw new InvalidOperationException("Custom control constructor requires design mode.");
        Child = new TextBlock { Name = "CustomControlText", Text = "Built custom control" };
    }
}

public static class BlockingStartup
{
    public static AppBuilder BuildAvaloniaApp()
    {
        if (Environment.GetEnvironmentVariable("AVDT_PREVIEW_TEST_PID") is { } path)
            File.WriteAllText(path, Environment.ProcessId.ToString(), System.Text.Encoding.UTF8);
        Thread.Sleep(TimeSpan.FromSeconds(60));
        return Program.BuildAvaloniaApp();
    }
}
