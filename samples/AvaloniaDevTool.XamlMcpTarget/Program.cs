using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;
using XamlMcp.Avalonia;

namespace AvaloniaDevTool.XamlMcpTarget;

public static class Program
{
    internal static string? ReadyFile { get; private set; }
    internal static int ExtraNodes { get; private set; } = 4;

    [STAThread]
    public static int Main(string[] args)
    {
        var headless = args.Contains("--headless");
        var inspection = args.Contains("--enable-inspection");
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--ready-file":
                    if (++index == args.Length) return Usage();
                    ReadyFile = Path.GetFullPath(args[index]);
                    break;
                case "--discovery-dir":
                    if (++index == args.Length) return Usage();
                    Environment.SetEnvironmentVariable("XAML_MCP_DIR", Path.GetFullPath(args[index]));
                    break;
                case "--nodes":
                    if (++index == args.Length || !int.TryParse(args[index], out var count) || count is < 0 or > 256) return Usage();
                    ExtraNodes = count;
                    break;
            }
        }
        if (inspection) Environment.SetEnvironmentVariable("XAML_MCP", "1");
        var builder = AppBuilder.Configure<TargetApplication>();
        builder = headless
            ? builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            : builder.UsePlatformDetect();
        builder = builder.AttachXamlMcp();
        return builder.StartWithClassicDesktopLifetime(args);
    }

    internal static void WriteReady()
    {
        var data = new
        {
            pid = Environment.ProcessId,
            endpoint = "pid:" + Environment.ProcessId,
            avaloniaVersion = typeof(Application).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            agentPackageVersion = typeof(InspectorService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        };
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Console.WriteLine(json);
        if (ReadyFile is null) return;
        using var stream = new FileStream(ReadyFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(stream, data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: XamlMcpTarget [--headless] [--enable-inspection] [--discovery-dir PATH] [--ready-file PATH] [--nodes 0..256]");
        return 1;
    }
}

