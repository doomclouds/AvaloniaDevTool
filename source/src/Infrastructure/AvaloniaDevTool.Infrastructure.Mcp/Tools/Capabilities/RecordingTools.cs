using System.ComponentModel;
using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using AvaloniaDevTool.Protocol;
using AvaloniaDevTool.Recording;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

[McpServerToolType]
public sealed class RecordingTools(ToolSession session, ManagedToolContext context)
    : InteractionToolBase(session, context)
{
    // Base64 and its UTF-16 JSON representation also stay below the LOH threshold.
    internal const int TransferChunkBytes = 16 * 1024;
    [McpServerTool(Name = "recording-start", ReadOnly = false, Destructive = false),
     Description("Starts streaming H.264 MP4 recording of the attached application's main window. Optional nodeIds select the padded bounds of up to 16 controls in one window; windowHandle instead selects a known native HWND of the attached process. outputDirectory is optional and belongs to the selected execution computer. Mouse clicks, including Agent framework input, are drawn inside the video. Returns starting/recording; poll status before reproducing. Hardware requested and verified are distinct facts.")]
    public async Task<RecordingStatus> Start(long[]? nodeIds = null,
        long? windowHandle = null,
        string? outputDirectory = null,
        int frameRate = 30,
        int bitrate = 3_000_000,
        int maxDurationSeconds = 120,
        long maxBytes = 134_217_728,
        bool mouseClicks = true,
        double paddingDip = 12,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new McpException("unsupported_platform: Window recording requires Windows.");
        if (nodeIds is { Length: > 0 } && windowHandle is not null)
            throw new McpException("invalid_argument: Specify nodeIds or windowHandle, not both.");
        if (nodeIds is { Length: > 16 } || frameRate is < 1 or > 60 ||
            bitrate is < 100_000 or > 20_000_000 || maxDurationSeconds is < 1 or > 1800 ||
            maxBytes is < 2_097_152 or > 536_870_912 || !double.IsFinite(paddingDip) || paddingDip is < 0 or > 256)
            throw new McpException("invalid_argument: Use at most 16 controls, 1..60 fps, 100000..20000000 bitrate, 1..1800 seconds, 2..512 MiB, and paddingDip 0..256.");
        if (outputDirectory is not null && (!Path.IsPathFullyQualified(outputDirectory) || string.IsNullOrWhiteSpace(outputDirectory)))
            throw new McpException("invalid_path: outputDirectory must be an absolute directory on the execution computer.");
        session.EnsureRecordingAllowsTargetChange();
        var expectedSession = (await session.GetAsync(cancellationToken).ConfigureAwait(false)).Session!;
        RecordingStartRequest request;
        if (nodeIds is { Length: > 0 })
        {
            request = await managed.WithClientAsync(cancellationToken, async (client, token) =>
            {
                var region = await client.Rendering.ResolveRecordingRegionAsync(
                    new ElementRecordingRegionRequest(nodeIds.Select(managed.NativeNodeId).ToArray(), paddingDip), token)
                    .ConfigureAwait(false);
                var bounds = region.RegionClientBounds;
                return new RecordingStartRequest
                {
                    ProcessId = region.ProcessId, StartTimeUtc = region.ProcessStartTimeUtc,
                    WindowHandle = region.WindowHandle,
                    Crop = new RecordingCrop(bounds.X, bounds.Y, bounds.Width, bounds.Height)
                };
            }).ConfigureAwait(false);
        }
        else
        {
            var client = await session.GetAsync(cancellationToken).ConfigureAwait(false);
            var identity = client.Session!;
            var expected = session.RecordingTargetIdentity ??
                throw new McpException("target_identity_unavailable: Attach again to verify the recording process lifetime.");
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                if (expected.ProcessId != identity.ProcessId || process.StartTime.ToUniversalTime() != expected.StartTimeUtc.UtcDateTime)
                    throw new McpException("target_changed: The attached process exited or its ID was reused.");
                var handle = windowHandle ?? process.MainWindowHandle.ToInt64();
                if (handle == 0)
                    throw new McpException("window_not_found: Supply a windowHandle from native-windows or select controls.");
                request = new RecordingStartRequest
                {
                    ProcessId = identity.ProcessId, StartTimeUtc = expected.StartTimeUtc,
                    WindowHandle = handle
                };
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                throw new McpException($"target_unavailable: {error.Message}");
            }
        }
        return await session.StartRecordingAsync(request with
        {
            OutputDirectory = outputDirectory, FrameRate = frameRate, Bitrate = bitrate,
            MaxDurationSeconds = maxDurationSeconds, MaxBytes = maxBytes, MouseClicks = mouseClicks
        }, expectedSession, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(Name = "recording-status", ReadOnly = true, Destructive = false),
     Description("Reads recorder state without calling the target UI thread. Omit recordingId for this session's latest recording. Completed metadata can also be read by the id returned from recording-list. A valid MP4 does not establish bug-fix success.")]
    public Task<RecordingStatus> Status(string? recordingId = null, CancellationToken cancellationToken = default) =>
        session.ReadRecordingAsync(recordingId, false, cancellationToken);

    [McpServerTool(Name = "recording-stop", ReadOnly = false, Destructive = false),
     Description("Requests an idempotent stop of this session's recording. The MP4 may still be finalizing; poll recording-status until completed or failed. Remains usable while the target application is paused.")]
    public Task<RecordingStatus> Stop(string? recordingId = null, CancellationToken cancellationToken = default) =>
        session.ReadRecordingAsync(recordingId, true, cancellationToken);

    [McpServerTool(Name = "recording-list", ReadOnly = true, Destructive = false),
     Description("Lists saved recording metadata on the selected execution computer, including paths, state and artifact ids. Recordings in a custom outputDirectory remain indexed. Does not require an attached application.")]
    public RecordingStatus[] List(int limit = 100)
    {
        try { return RecordingArtifacts.List(limit); }
        catch (RecordingException error) { throw new McpException($"{error.Code}: {error.Message}"); }
    }

    [McpServerTool(Name = "recording-export", ReadOnly = false, Destructive = false),
     Description("Exports a completed recording to a caller-local destinationPath through the stdio gateway. Omit the path for the caller's default recording download directory. The gateway verifies length and SHA-256; it never returns a remote path as a downloaded file. offset/maxBytes are bounded transfer fields used by the gateway; direct serve clients receive chunks.")]
    public async Task<CallToolResult> Export(string artifactId,
        string? destinationPath = null, long offset = 0, int maxBytes = TransferChunkBytes,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || maxBytes is < 1 or > TransferChunkBytes)
            throw new McpException("invalid_argument: offset must be nonnegative and maxBytes must be 1..16384.");
        RecordingStatus artifact;
        try { artifact = RecordingArtifacts.Get(artifactId); }
        catch (RecordingException error) { throw new McpException($"{error.Code}: {error.Message}"); }
        if (artifact.State != "completed" || artifact.Sha256 is null)
            throw new McpException("recording_not_ready: Wait until the recording is completed before exporting.");
        try
        {
            await using var file = new FileStream(artifact.OutputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length != artifact.BytesWritten || offset > file.Length)
                throw new McpException("recording_changed: The saved file no longer matches its index.");
            file.Position = offset;
            var count = (int)Math.Min(maxBytes, file.Length - offset);
            var bytes = ArrayPool<byte>.Shared.Rent(maxBytes);
            try
            {
                await file.ReadExactlyAsync(bytes.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                // Serialize while we still own the rented buffer; only the independent JSON text escapes.
                var text = JsonSerializer.Serialize(new RecordingExportView(artifactId, offset,
                    bytes.AsMemory(0, count), file.Length, artifact.Sha256, offset + count == file.Length));
                return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
            }
            finally { ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new McpException($"recording_export_failed: {error.Message}");
        }
    }
}

public sealed record RecordingExportChunk(string ArtifactId, long Offset, byte[] Bytes,
    long TotalBytes, string Sha256, bool IsComplete);

internal readonly record struct RecordingExportView(string ArtifactId, long Offset, ReadOnlyMemory<byte> Bytes,
    long TotalBytes, string Sha256, bool IsComplete);
