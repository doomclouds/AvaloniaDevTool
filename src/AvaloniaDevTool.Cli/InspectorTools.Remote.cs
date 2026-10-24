using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "attach-to-remote"), Description("Connect to an opt-in Native WebSocket diagnostics endpoint. tokenEnvironmentVariable names an environment variable in this MCP server process; never supply the token in the URL or tool arguments. Plain ws is restricted to literal loopback; remote hosts require normal trusted TLS. Detach releases the session without stopping the target application.")]
    public Task<SessionInfo> AttachRemote(string endpoint, string tokenEnvironmentVariable, CancellationToken cancellationToken) =>
        session.AttachRemoteAsync(endpoint, tokenEnvironmentVariable, cancellationToken);
}
