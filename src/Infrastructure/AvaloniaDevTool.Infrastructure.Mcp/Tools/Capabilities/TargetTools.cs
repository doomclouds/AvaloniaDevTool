using AvaloniaDevTool.Client;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace AvaloniaDevTool.Cli;

[McpServerToolType]
public sealed class TargetTools(ToolSession session, ManagedToolContext context) : ManagedToolBase(session, context)
{
    [McpServerTool(Name = "attach-to-app", ReadOnly = false, Destructive = false),
     Description(
         "Connects to a discovered running Avalonia application. Omit id or pass 0 to list application endpoints; use an id returned by that list, not an assumed operating-system process id.")]
    public Task<ApplicationAttachmentResult> AttachToApp(long id = 0, CancellationToken cancellationToken = default) =>
        WithSessionAsync<ApplicationAttachmentResult>(cancellationToken,
            async (owner, token) =>
            {
                if (id < 0)
                    throw new McpException("invalid_argument: id must be zero or a discovered application identifier.");
                var applications = owner.DiscoverApplications();
                if (id == 0)
                    return new(false,
                        null,
                        applications.Applications,
                        applications.Truncated,
                        "If your application is absent, reference AvaloniaDevTool.Agent and call DiagnosticsAgent.Attach after Avalonia startup. A package reference alone does not start an endpoint; no F12 shortcut is required.");
                var target = applications.Applications.SingleOrDefault(app => app.Id == id)
                             ?? throw new McpException(
                                 "app_not_found: List applications again and use an identifier from the current list.");
                var attached = await owner.AttachAsync(target.PipeName, token).ConfigureAwait(false);
                owner.RememberApplication(target.PipeName, attached);
                return new(true, attached, owner.DiscoverApplications().Applications, applications.Truncated);
            });

    [McpServerTool(Name = "detach", ReadOnly = false, Destructive = false),
     Description(
         "Disconnects the current application or owned preview session and releases temporary diagnostic changes.")]
    public Task<string> Detach(CancellationToken cancellationToken = default) =>
        WithSessionAsync(cancellationToken,
            async (owner, _) =>
            {
                await owner.DetachAsync().ConfigureAwait(false);
                return "Disconnected";
            });
}
