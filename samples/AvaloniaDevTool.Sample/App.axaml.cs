using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
#if DEBUG
using AvaloniaDevTool.Agent;
using Avalonia.Threading;
#endif

namespace AvaloniaDevTool.Sample;

public sealed partial class App : Application
{
#if DEBUG
    private DiagnosticsAgent? _agent;
#endif

    public override void Initialize()
    {
        _ = typeof(Avalonia.Fonts.Inter.InterFontCollection).Assembly;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;
#if DEBUG
            _agent = DiagnosticsAgent.Attach(this, new DiagnosticsAgentOptions
            {
                PipeName = Program.PipeName,
                InspectorExecutablePath = SampleInspectorLocator.Find(),
                ApplicationName = "Avalonia 测试工作台",
                LogSources = [SampleDiagnostics.Provider, SampleDiagnostics.CustomSource]
            });
            if (Program.EnableRemote) _ = StartRemoteAsync(_agent, mainWindow);
            mainWindow.ViewModel.AgentStatus = "Debug · 等待 Inspector 连接";
            var statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            statusTimer.Tick += (_, _) => mainWindow.ViewModel.AgentStatus = _agent.IsConnected
                ? "Debug · Inspector 已连接"
                : "Debug · 等待 Inspector 连接";
            statusTimer.Start();
            desktop.Exit += (_, _) =>
            {
                statusTimer.Stop();
                _agent.Dispose();
                StopRemote();
                SampleDiagnostics.Dispose();
            };
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }
}

