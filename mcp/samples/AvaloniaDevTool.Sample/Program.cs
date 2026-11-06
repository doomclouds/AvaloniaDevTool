using Avalonia;

namespace AvaloniaDevTool.Sample;

internal static class Program
{
    public static string PipeName { get; private set; } = $"avalonia-devtool-{Environment.ProcessId}";
    public static bool EnableRemote { get; private set; }
    public static int RemotePort { get; private set; }
    public static string? RemoteTokenEnvironmentVariable { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--remote")
            {
                EnableRemote = true;
                continue;
            }

            if (args[i] == "--remote-port")
            {
                if (++i >= args.Length || !int.TryParse(args[i], out var port) || port is < 0 or > 65535)
                    return 1;
                RemotePort = port;
                continue;
            }

            if (args[i] == "--remote-token-env")
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    return 1;
                RemoteTokenEnvironmentVariable = args[i];
                continue;
            }

            if (args[i] == "--pipe")
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                {
                    Console.Error.WriteLine("Usage: AvaloniaDevTool.Sample [--pipe NAME]");
                    return 1;
                }

                PipeName = args[i];
            }
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();
}
