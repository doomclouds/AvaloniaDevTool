using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using AvaloniaDevTool.Agent;

namespace AvaloniaDevTool.AotSample;

internal static class Program
{
    internal static string PipeName { get; private set; } = $"avalonia-devtool-aot-{Environment.ProcessId}";
    internal static string? ReadyFile { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--pipe" && i + 1 < args.Length)
                PipeName = args[++i];
            else if (args[i] == "--ready-file" && i + 1 < args.Length)
                ReadyFile = args[++i];
            else
                throw new ArgumentException("Usage: AvaloniaDevTool.AotSample [--pipe NAME] [--ready-file PATH]");
        }

        return AppBuilder.Configure<AotApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
    }
}

public sealed class AotApplication : Application
{
    private DiagnosticsAgent? _agent;
    public override void Initialize() => Styles.Add(new SimpleTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var status = new TextBlock { Name = "AotStatus", FontSize = 22, Foreground = Brushes.DarkSlateBlue };
            status.Styles.Add(new Style(selector => selector.OfType<TextBlock>())
            {
                Setters = { new Setter(TextBlock.TextProperty, "Native diagnostics ready") }
            });
            var window = new Window
            {
                Name = "AotWindow", Title = "AvaloniaDevTool Native AOT consumer", Width = 520, Height = 280,
                Content = new StackPanel
                {
                    Name = "AotPanel", Margin = new Thickness(24), Spacing = 16,
                    Children =
                    {
                        status,
                        new TextBox { Name = "AotInput", Text = "Editable consumer value" },
                        new Button { Name = "AotButton", Content = "Native AOT target" }
                    }
                }
            };
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            // This dedicated diagnostics consumer intentionally enables the Agent in Release.
            _agent = DiagnosticsAgent.Attach(this,
                new DiagnosticsAgentOptions
                {
                    PipeName = Program.PipeName,
                    ApplicationName = "Native AOT consumer"
                });
            window.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    if (Program.ReadyFile is { } path)
                        File.WriteAllText(path,
                            $"pid={Environment.ProcessId}\npipe={Program.PipeName}\ndynamicCodeSupported={RuntimeFeature.IsDynamicCodeSupported}\ndynamicCodeCompiled={RuntimeFeature.IsDynamicCodeCompiled}\n",
                            System.Text.Encoding.UTF8);
                },
                DispatcherPriority.Background);
            desktop.Exit += (_, _) => _agent.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
