using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using AvaloniaDevTool.Agent;

namespace AvaloniaDevTool.AutomationSample;

internal static class Program
{
    internal static string PipeName { get; private set; } = $"avalonia-devtool-automation-{Environment.ProcessId}";
    internal static string? TestRoot { get; private set; }
    internal static string? ReadyFile { get; private set; }
    internal static string? ResultFile { get; private set; }
    internal static string? IoGateDirectory { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                var option = args[i];
                if (option is not ("--pipe" or "--test-root" or "--ready-file" or "--result-file" or "--io-gate") || ++i >= args.Length || string.IsNullOrWhiteSpace(args[i]))
                    throw new ArgumentException("Usage: AvaloniaDevTool.AutomationSample [--pipe NAME] [--test-root DIRECTORY] [--ready-file PATH] [--result-file PATH] [--io-gate DIRECTORY (test only)]");
                switch (option)
                {
                    case "--pipe": PipeName = args[i]; break;
                    case "--test-root": TestRoot = Path.GetFullPath(args[i]); break;
                    case "--ready-file": ReadyFile = Path.GetFullPath(args[i]); break;
                    case "--result-file": ResultFile = Path.GetFullPath(args[i]); break;
                    case "--io-gate": IoGateDirectory = Path.GetFullPath(args[i]); break;
                }
            }
            if (PipeName.IndexOfAny(['/', '\\', ':']) >= 0)
                throw new ArgumentException("The pipe name must be a simple local pipe name.");
            if (TestRoot is not null)
                Directory.CreateDirectory(TestRoot);
            return AppBuilder.Configure<AutomationApplication>().UsePlatformDetect().LogToTrace()
                .StartWithClassicDesktopLifetime([]);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}

public sealed class AutomationApplication : Application
{
    private DiagnosticsAgent? _agent;

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
            // This dedicated acceptance target intentionally exposes the Agent in Release.
            _agent = DiagnosticsAgent.Attach(this, new DiagnosticsAgentOptions
            {
                PipeName = Program.PipeName,
                ApplicationName = "Avalonia automation acceptance sample"
            });
            mainWindow.Opened += (_, _) =>
            {
                if (Program.ReadyFile is { } readyFile)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(readyFile)!);
                    File.WriteAllText(readyFile,
                        $"pid={Environment.ProcessId}\npipe={Program.PipeName}\ntestRoot={Program.TestRoot}\n",
                        new UTF8Encoding(false));
                }
            };
            desktop.Exit += (_, _) => _agent.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
