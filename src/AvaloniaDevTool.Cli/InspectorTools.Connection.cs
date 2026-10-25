using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "apps", ReadOnly = true), Description("List discoverable local AvaloniaDevTool pipe names on Windows. Other platforms accept a manually supplied pipe name.")]
    public IReadOnlyList<string> Apps() => OperatingSystem.IsWindows()
        ? Directory.GetFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>()
            .Where(name => name.StartsWith(DiagnosticsProtocol.PipePrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()
        : [];

    [McpServerTool(Name = "attach-to-app", ReadOnly = true), Description("Connect this MCP session to an application using its AvaloniaDevTool pipe name. The current Agent supports one inspector connection at a time.")]
    public Task<SessionInfo> AttachToApp(string pipeName, CancellationToken cancellationToken) => session.AttachAsync(pipeName, cancellationToken);

    [McpServerTool(Name = "detach"), Description("Disconnect and release this session's temporary property, resource and pseudo-class edits.")]
    public async Task<string> Detach() { await session.DetachAsync(); return "Disconnected"; }
}
