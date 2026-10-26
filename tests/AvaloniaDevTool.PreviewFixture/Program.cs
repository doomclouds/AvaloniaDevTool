using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml;
using Avalonia.Themes.Fluent;

namespace AvaloniaDevTool.PreviewFixture;

public static class Program
{
    public static void Main(string[] args)
    {
        if (!args.SequenceEqual(new[] { "--without-diagnostics" }))
            throw new InvalidOperationException("Application Main must never execute during preview.");
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        var window = new Window { Content = new TextBlock { Text = "Application without a diagnostics bridge" } };
        window.Show();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            ready = window.IsVisible,
            processId = Environment.ProcessId,
            agentReferenced = typeof(Program).Assembly.GetReferencedAssemblies()
                .Any(assembly => assembly.Name == "AvaloniaDevTool.Agent")
        }));
        Console.Out.Flush();
        Console.ReadLine();
        window.Close();
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        if (!Design.IsDesignMode)
            throw new InvalidOperationException("The builder requires the real designer mode flag.");
        Console.WriteLine("Target BuildAvaloniaApp stdout is deliberately noisy.");
        return AppBuilder.Configure<FixtureApp>().UsePlatformDetect();
    }

    public static void PublishPid(string path)
    {
        var temporary = path + ".pending";
        File.WriteAllText(temporary, Environment.ProcessId.ToString(), System.Text.Encoding.UTF8);
        File.Move(temporary, path, overwrite: true);
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
        if (!Design.IsDesignMode)
            throw new InvalidOperationException("Custom control constructor requires design mode.");
        Child = new TextBlock { Name = "CustomControlText", Text = "Built custom control" };
    }
}

public static class BlockingStartup
{
    public static AppBuilder BuildAvaloniaApp()
    {
        if (Environment.GetEnvironmentVariable("AVDT_PREVIEW_TEST_PID") is { } path)
            Program.PublishPid(path);
        Thread.Sleep(TimeSpan.FromSeconds(60));
        return Program.BuildAvaloniaApp();
    }
}
