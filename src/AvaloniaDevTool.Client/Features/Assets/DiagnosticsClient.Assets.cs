using System.Security.Cryptography;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    /// <summary>Copies the full asset into a caller-owned stream and verifies its length and SHA-256.</summary>
    public async Task<AssetExportResult> ExportAssetAsync(string uri,
        Stream destination,
        IProgress<AssetExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination must be writable.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        var peer = _peer ?? throw new RpcException("not_connected", "Connect to a diagnostic application first.");
        string? token = null;
        try
        {
            // Receive the token even if cancellation races with begin, so the reader can always be closed.
            var begin = await peer
                .InvokeAsync<BeginAssetExportResult>(AssetExportMethods.Begin, new BeginAssetExportRequest(uri))
                .ConfigureAwait(false);
            token = begin.Token;
            if (begin.ChunkBytes is <= 0 or > AssetExportMethods.ChunkBytes ||
                begin.MaximumBytes > AssetExportMethods.MaximumBytes ||
                begin.TotalBytes is < 0 or > AssetExportMethods.MaximumBytes)
                throw new RpcException("invalid_response", "The export limits are invalid.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            progress?.Report(new AssetExportProgress(0, begin.TotalBytes));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = await peer.InvokeAsync<AssetExportChunk>(AssetExportMethods.Read,
                    new ReadAssetExportRequest(token, written),
                    cancellationToken).ConfigureAwait(false);
                if (chunk.Offset != written || chunk.Bytes.Length > begin.ChunkBytes ||
                    written + chunk.Bytes.Length > AssetExportMethods.MaximumBytes ||
                    (!chunk.IsComplete && chunk.Bytes.Length == 0))
                    throw new RpcException("invalid_response", "The export returned an invalid chunk.");
                await destination.WriteAsync(chunk.Bytes, cancellationToken).ConfigureAwait(false);
                hash.AppendData(chunk.Bytes);
                written += chunk.Bytes.Length;
                progress?.Report(new AssetExportProgress(written, chunk.TotalBytes ?? begin.TotalBytes));
                if (!chunk.IsComplete)
                    continue;
                var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (chunk.TotalBytes != written || (begin.TotalBytes is { } expected && expected != written) ||
                    !string.Equals(chunk.Sha256, digest, StringComparison.OrdinalIgnoreCase))
                    throw new RpcException("asset_integrity_failed",
                        "The exported asset length or SHA-256 does not match the target stream.");
                return new AssetExportResult(uri, written, digest);
            }
        }
        finally
        {
            if (token is not null)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await peer.InvokeAsync<OperationResult>(AssetExportMethods.Close,
                        new CloseAssetExportRequest(token),
                        cleanup.Token).ConfigureAwait(false);
                }
                catch
                {
                    /* The session also disposes readers when the transport disconnects. */
                }
            }
        }
    }

    /// <summary>Commits a verified local export atomically. Existing files require an explicit overwrite choice.</summary>
    public async Task<AssetExportResult> ExportAssetToFileAsync(string uri,
        string destinationPath,
        bool overwrite = false,
        IProgress<AssetExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(path)!;
        var temporary = Path.Combine(directory, ".avdt-export-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            AssetExportResult result;
            await using (var stream = new FileStream(temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             65536,
                             FileOptions.Asynchronous))
            {
                result = await ExportAssetAsync(uri, stream, progress, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite);
            return result;
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
