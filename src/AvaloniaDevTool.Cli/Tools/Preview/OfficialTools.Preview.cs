using System.ComponentModel;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class OfficialTools
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
