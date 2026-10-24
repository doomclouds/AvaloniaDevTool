using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(AvaloniaDevTool.Inspector.Tests.TestAppBuilder))]

namespace AvaloniaDevTool.Inspector.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

