using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AvaloniaDevTool.Agent;

public sealed partial class DiagnosticsAgent
{
    private IDisposable? _launcherRegistration;
    private Process? _launchedInspector;
    private long _lastLaunchTimestamp;

    private void InitializeLauncher()
    {
        if (_options.InspectorLauncher is null && string.IsNullOrWhiteSpace(_options.InspectorExecutablePath))
            return;
        _launcherRegistration = InputElement.KeyDownEvent.AddClassHandler(typeof(TopLevel),
            (_, args) =>
            {
                if (Volatile.Read(ref _disposed) != 0 || args is not KeyEventArgs key ||
                    !_options.LaunchGesture.Matches(key) || key.Source is not Control source)
                    return;
                var topLevel = TopLevel.GetTopLevel(source);
                var roots = _options.TopLevelProvider?.Invoke() ??
                    (_application.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.Cast<TopLevel>() ?? [];
                if (topLevel is null || !roots.Contains(topLevel)) return;
                args.Handled = true;
                if (IsConnected || (_lastLaunchTimestamp != 0 && Stopwatch.GetElapsedTime(_lastLaunchTimestamp) < TimeSpan.FromSeconds(1)))
                    return;
                _lastLaunchTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    if (_options.InspectorLauncher is { } launch) launch(PipeName);
                    else
                    {
                        if (_launchedInspector is { HasExited: false }) return;
                        var path = _options.InspectorExecutablePath!;
                        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                            throw new FileNotFoundException("Configure an existing absolute InspectorExecutablePath.", path);
                        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
                        start.ArgumentList.Add("--pipe");
                        start.ArgumentList.Add(PipeName);
                        _launchedInspector?.Dispose();
                        _launchedInspector = Process.Start(start);
                    }
                    LastError = null;
                }
                catch (Exception exception) { LastError = "Inspector launch failed: " + exception.Message; }
            }, RoutingStrategies.Tunnel, true);
    }

    private void DisposeLauncher()
    {
        _launcherRegistration?.Dispose();
        _launcherRegistration = null;
        _launchedInspector?.Dispose();
        _launchedInspector = null;
    }
}

