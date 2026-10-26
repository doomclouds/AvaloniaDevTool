using System.ComponentModel;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "export-asset"),
     Description(
         "Export a complete embedded target asset to an absolute path on this MCP server's computer. Uses bounded chunks and verifies byte length and SHA-256; the preview truncation limit does not truncate exported files. Existing files require explicit overwrite=true.")]
    public async Task<AssetExportResult> ExportAsset(string uri,
        string destinationPath,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(destinationPath))
            throw new McpException("invalid_path: destinationPath must be an absolute local path.");
        return await (await session.GetAsync(cancellationToken)).ExportAssetToFileAsync(uri,
            destinationPath,
            overwrite,
            cancellationToken: cancellationToken);
    }
}
