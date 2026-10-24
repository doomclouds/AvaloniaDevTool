using Avalonia;

namespace AvaloniaDevTool.Inspector;

internal static class Program
{
    internal static InspectorLaunchOptions Options { get; private set; } = new();
    internal static string? InitialPipeName => Options.InitialEndpoint;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("Avalonia Inspector: --pipe NAME | --endpoint ws(s)://HOST/PATH [--token-env VARIABLE] | --backend xamlmcp --target PID_OR_INSTANCE --server ABSOLUTE_SERVER.dll_OR.exe");
            return;
        }
        try { Options = InspectorLaunchOptions.Parse(args); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Environment.ExitCode = 2;
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}

