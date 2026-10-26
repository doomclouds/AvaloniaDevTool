using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Cli;

public sealed partial class ToolSession
{
    public SessionInfo? CurrentSession => _client.Session;
    public bool IsConnected => _client.IsConnected;
    public long CurrentContentGeneration => _client.CurrentContentGeneration;
}
