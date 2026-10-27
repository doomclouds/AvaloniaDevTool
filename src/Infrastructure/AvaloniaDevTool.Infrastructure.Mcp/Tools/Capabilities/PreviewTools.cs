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
public sealed class PreviewTools(ToolSession session, ManagedToolContext context) : ManagedToolBase(session, context)
{
    [McpServerTool(Name = "attach-to-file"),
     Description(
         "Opens a previewer for a XAML file in its project. Optionally builds the project before starting the previewer. Saves to the XAML file update the connected preview without rebuilding.")]
    public Task<PreviewAttachment> AttachToFile(string xamlFilePath,
        string xamlProjectPath,
        bool build = false,
        CancellationToken cancellationToken = default) =>
        WithSessionAsync(cancellationToken,
            (owner, token) => owner.AttachProjectFileAsync(xamlFilePath, xamlProjectPath, build, token));
}
