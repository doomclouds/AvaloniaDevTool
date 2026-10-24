using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "attach-to-file"), Description("Preview an absolute AXAML file using its built source-project assembly and project file. applicationAssemblyPath can select a distinct startup application for a referenced control library. Initializes the App in design mode, resolves resources and x:Class, and watches file saves. Runs in an owned child process; detach or MCP exit stops it. Does not build the project or run Main. headless=true renders real Skia pixels without desktop windows.")]
    public Task<PreviewAttachment> AttachToFile(string filePath, string assemblyPath, string projectPath,
        bool headless = false, bool watch = true, string? applicationAssemblyPath = null, string? applicationType = null, int timeoutSeconds = 30,
        CancellationToken cancellationToken = default) =>
        session.AttachFileAsync(filePath, assemblyPath, projectPath, headless, watch, applicationAssemblyPath, applicationType, timeoutSeconds, cancellationToken);
}
