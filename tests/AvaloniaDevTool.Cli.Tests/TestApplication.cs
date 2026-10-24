using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(AvaloniaDevTool.Cli.Tests.TestAppBuilder))]

namespace AvaloniaDevTool.Cli.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
public sealed class TestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

