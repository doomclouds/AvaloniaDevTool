using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

public sealed class DiagnosticsAgentOptions
{
    public string PipeName { get; init; } = DiagnosticsProtocol.PipePrefix + Environment.ProcessId;
    public string ApplicationName { get; init; } = AppDomain.CurrentDomain.FriendlyName;
    public KeyGesture LaunchGesture { get; init; } = new(Key.F12);
    public string? InspectorExecutablePath { get; init; }
    public Action<string>? InspectorLauncher { get; init; }
    public IReadOnlyList<IDiagnosticsLogSource> LogSources { get; init; } = [];

    // A custom host can supply its roots without a desktop application lifetime.
    public Func<IEnumerable<TopLevel>>? TopLevelProvider { get; init; }
}

