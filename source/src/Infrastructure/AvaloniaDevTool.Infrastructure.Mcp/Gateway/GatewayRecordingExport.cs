using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli.Gateway;

internal sealed partial class GatewaySession
{
    private static async Task<CallToolResult> ExportRecordingAsync(IMcpExecutor executor,
        RequestContext<CallToolRequestParams> request, CallToolRequestParams parameters, CancellationToken token)
    {
        var arguments = parameters.Arguments;
        var artifactId = arguments is not null && arguments.TryGetValue("artifactId", out var id)
            ? id.GetString() : null;
        if (artifactId is null || !artifactId.StartsWith("recording-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(artifactId[10..], "N", out _))
            throw new McpException("recording_not_found: Supply an artifactId from recording-list.");
        var destination = arguments is not null && arguments.TryGetValue("destinationPath", out var path) &&
                          path.ValueKind != JsonValueKind.Null ? path.GetString() : null;
        destination ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AvaloniaDevTool", "recording-downloads", artifactId + ".mp4");
        if (!Path.IsPathFullyQualified(destination))
            throw new McpException("invalid_path: destinationPath must be an absolute file path on the caller's computer.");
        if (File.Exists(destination))
            throw new McpException("file_exists: Choose a new destinationPath; existing files are not overwritten.");
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, ".avdt-recording-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            CallToolResult? lastResult = null;
            string? expectedHash = null;
            long expectedLength = -1;
            long written = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             65536, FileOptions.Asynchronous))
            {
                while (true)
                {
                    lastResult = await executor.InvokeAsync(request, new CallToolRequestParams
                    {
                        Name = "recording-export", Arguments = new Dictionary<string, JsonElement>
                        {
                            ["artifactId"] = JsonSerializer.SerializeToElement(artifactId),
                            ["offset"] = JsonSerializer.SerializeToElement(written),
                            ["maxBytes"] = JsonSerializer.SerializeToElement(RecordingTools.TransferChunkBytes)
                        }
                    }, token).ConfigureAwait(false);
                    if (lastResult.IsError == true)
                        return lastResult;
                    var text = lastResult.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
                    var chunk = text is null ? null : JsonSerializer.Deserialize<RecordingExportChunk>(text, ExecutionScopeTool.Json);
                    if (chunk is null || chunk.ArtifactId != artifactId || chunk.Offset != written ||
                        chunk.Bytes.Length > RecordingTools.TransferChunkBytes || chunk.TotalBytes is < 1 or > 536_870_912 ||
                        written + chunk.Bytes.Length > chunk.TotalBytes ||
                        (!chunk.IsComplete && chunk.Bytes.Length == 0))
                        throw new McpException("recording_export_invalid: The executor returned an invalid file chunk.");
                    if (expectedLength < 0)
                    {
                        expectedLength = chunk.TotalBytes;
                        expectedHash = chunk.Sha256;
                    }
                    if (expectedLength != chunk.TotalBytes || expectedHash != chunk.Sha256)
                        throw new McpException("recording_changed: The recording changed during export.");
                    await file.WriteAsync(chunk.Bytes, token).ConfigureAwait(false);
                    hash.AppendData(chunk.Bytes);
                    written += chunk.Bytes.Length;
                    if (chunk.IsComplete)
                        break;
                }
                await file.FlushAsync(token).ConfigureAwait(false);
            }
            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (written != expectedLength || !string.Equals(digest, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new McpException("recording_integrity_failed: The exported file length or SHA-256 does not match.");
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, false);
            var document = JsonSerializer.SerializeToNode(lastResult!, ExecutionScopeTool.Json)!.AsObject();
            var result = new CallToolResult
            {
                Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new
                {
                    ArtifactId = artifactId, Path = destination, ByteLength = written, Sha256 = digest, Complete = true
                }) }]
            };
            var resultNode = JsonSerializer.SerializeToNode(result, ExecutionScopeTool.Json)!.AsObject();
            if (document["_meta"] is { } metadata)
                resultNode["_meta"] = metadata.DeepClone();
            return resultNode.Deserialize<CallToolResult>(ExecutionScopeTool.Json)!;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new McpException($"recording_export_failed: {error.Message}");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
