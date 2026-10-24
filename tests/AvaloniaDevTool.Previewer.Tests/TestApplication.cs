using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(AvaloniaDevTool.Previewer.Tests.PreviewTestAppBuilder))]

namespace AvaloniaDevTool.Previewer.Tests;

public static class PreviewTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<PreviewTestApplication>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public sealed class PreviewTestApplication : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

