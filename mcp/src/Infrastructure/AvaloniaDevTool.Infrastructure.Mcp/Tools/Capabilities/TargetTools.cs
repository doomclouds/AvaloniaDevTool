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
         "Discovers existing Agent endpoints and Windows desktop CoreCLR candidates. Omit id or pass 0 to list; select an id from that list, not an assumed PID. A candidate marked requiresInjection is confirmed as Avalonia and hot-attached through the native bootstrap without modifying or restarting the app. Current native support: Windows x64, .NET 10, Avalonia 12.1.1/12.1.4. First use can build the matching Agent.")]
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
                        "Choose an id from this list. requiresInjection marks a desktop CoreCLR candidate; selecting it confirms Avalonia and loads the Agent through the native bootstrap. The target needs no Agent reference or startup code. Run the plugin setup build first.");
                var target = applications.Applications.SingleOrDefault(app => app.Id == id)
                             ?? throw new McpException(
                                 "app_not_found: List applications again and use an identifier from the current list.");
                var (attached, attachment) = await owner.AttachApplicationAsync(target, token).ConfigureAwait(false);
                owner.RememberApplication(target.PipeName, attached);
                return new(true, attached, owner.DiscoverApplications().Applications, applications.Truncated, Attachment: attachment);
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
