using System.Security.Cryptography;
using System.Text;
using System.IO.Pipes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class AssetExportTests
{
    [AvaloniaFact]
    public async Task Disconnect_invalidates_export_tokens_and_new_session_can_export()
    {
        var window = new Window();
        window.Show();
        try
        {
            await using var agent = Attach(window);
            var uri = AssetInspectionService.List().Single(item =>
                item.Uri.Contains("AvaloniaDevTool.Agent.Tests/Assets/asset-long-utf8.txt",
                    StringComparison.OrdinalIgnoreCase)).Uri;
            var pipe = new NamedPipeClientStream(".", agent.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            var peer = new RpcPeer(pipe);
            peer.Start();
            var begin = await peer.InvokeAsync<BeginAssetExportResult>(AssetExportMethods.Begin,
                new BeginAssetExportRequest(uri));
            await peer.DisposeAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (agent.IsConnected)
                await Task.Delay(15, timeout.Token);
            var secondPipe =
                new NamedPipeClientStream(".", agent.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await secondPipe.ConnectAsync(timeout.Token);
            await using var second = new RpcPeer(secondPipe);
            second.Start();
            var error = await Assert.ThrowsAsync<RpcException>(() =>
                second.InvokeAsync<AssetExportChunk>(AssetExportMethods.Read,
                    new ReadAssetExportRequest(begin.Token, 0)));
            Assert.Equal("asset_export_expired", error.Code);
            var next = await second.InvokeAsync<BeginAssetExportResult>(AssetExportMethods.Begin,
                new BeginAssetExportRequest(uri));
            Assert.NotEqual(begin.Token, next.Token);
            await second.InvokeAsync<OperationResult>(AssetExportMethods.Close,
                new CloseAssetExportRequest(next.Token));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Full_asset_export_crosses_preview_limit_and_preserves_bytes_hash_and_progress()
    {
        var window = new Window();
        window.Show();
        try
        {
            await using var agent = Attach(window);
            await using var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            var asset = (await client.GetAssetsAsync()).Single(item =>
                item.Uri.Contains("AvaloniaDevTool.Agent.Tests/Assets/asset-long-utf8.txt",
                    StringComparison.OrdinalIgnoreCase));
            var preview = await client.OpenAssetAsync(asset.Uri);
            Assert.True(preview.IsTruncated);
            Assert.Equal(1024 * 1024, preview.ByteLength);
            Assert.True(preview.TotalByteLength > preview.ByteLength);
            Assert.DoesNotContain("\uFFFD", preview.Text!);
            using var original = AssetLoader.Open(new Uri(asset.Uri));
            using var expected = new MemoryStream();
            original.CopyTo(expected);
            using var destination = new MemoryStream();
            var progress = new List<AssetExportProgress>();
            var result = await client.ExportAssetAsync(asset.Uri,
                destination,
                new InlineProgress(value => progress.Add(value)));
            Assert.Equal(expected.ToArray(), destination.ToArray());
            Assert.Equal(expected.Length, result.ByteLength);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(expected.ToArray())).ToLowerInvariant(), result.Sha256);
            Assert.True(progress.Count > 16);
            Assert.Equal(expected.Length, progress[^1].BytesWritten);
            Assert.True(destination.CanWrite);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Cancel_and_destination_error_close_readers_and_atomic_file_export_retains_old_file()
    {
        var window = new Window();
        window.Show();
        var folder = Path.Combine(Path.GetTempPath(), "avdt-asset-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var agent = Attach(window);
            await using var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            var uri = (await client.GetAssetsAsync()).Single(item =>
                item.Uri.Contains("AvaloniaDevTool.Agent.Tests/Assets/asset-long-utf8.txt",
                    StringComparison.OrdinalIgnoreCase)).Uri;
            for (var i = 0; i < 3; i++)
            {
                using var cancel = new CancellationTokenSource();
                using var bytes = new MemoryStream();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ExportAssetAsync(uri,
                    bytes,
                    new InlineProgress(value =>
                    {
                        if (value.BytesWritten > 0)
                            cancel.Cancel();
                    }),
                    cancel.Token));
                Assert.InRange(bytes.Length, 1, AssetExportMethods.ChunkBytes);
            }

            await Assert.ThrowsAsync<IOException>(() => client.ExportAssetAsync(uri, new FailingDestination()));
            var path = Path.Combine(folder, "existing.txt");
            await File.WriteAllTextAsync(path, "original", Encoding.UTF8);
            await Assert.ThrowsAsync<IOException>(() => client.ExportAssetToFileAsync(uri, path));
            Assert.Equal("original", await File.ReadAllTextAsync(path, Encoding.UTF8));
            Assert.Single(Directory.GetFiles(folder));
            var saved = await client.ExportAssetToFileAsync(uri, path, overwrite: true);
            Assert.Equal(saved.ByteLength, new FileInfo(path).Length);
            Assert.Equal(saved.Sha256,
                Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant());
            Assert.Single(Directory.GetFiles(folder));
        }
        finally
        {
            window.Close();
            foreach (var file in Directory.GetFiles(folder))
                File.Delete(file);
            Directory.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void Export_validates_owned_uris_and_disconnect_style_dispose_releases_streams()
    {
        using var service = new AssetExportService();
        foreach (var uri in new[]
                 {
                     "file:///C:/Windows/win.ini", "https://example.test/asset", "avares://Missing.Target/a.txt",
                     "avares://AvaloniaDevTool.Agent.Tests/Assets/missing.txt"
                 })
            Assert.Throws<RpcException>(() => service.Begin(new BeginAssetExportRequest(uri)));
        Assert.Equal(0, service.ActiveCount);
        var streams = new List<ProbeStream>();
        var local = new AssetExportService(_ =>
        {
            var stream = new ProbeStream(new byte[100]);
            streams.Add(stream);
            return stream;
        });
        var token = local.Begin(new BeginAssetExportRequest("test")).Token;
        local.Dispose();
        Assert.True(streams[0].WasDisposed);
        Assert.Equal(0, local.ActiveCount);
        Assert.Throws<ObjectDisposedException>(() => local.Read(new ReadAssetExportRequest(token, 0)));
    }

    [Fact]
    public void Sequential_nonseek_readers_expire_enforce_cap_and_recover_from_errors()
    {
        var now = DateTimeOffset.UtcNow;
        var streams = new List<ProbeStream>();
        using var service = new AssetExportService(_ =>
            {
                var stream = new ProbeStream(new byte[100]);
                streams.Add(stream);
                return stream;
            },
            () => now);
        var first = service.Begin(new BeginAssetExportRequest("test"));
        Assert.Null(first.TotalBytes);
        var second = service.Begin(new BeginAssetExportRequest("test"));
        Assert.Equal("asset_export_busy",
            Assert.Throws<RpcException>(() => service.Begin(new BeginAssetExportRequest("test"))).Code);
        Assert.Equal("asset_export_offset",
            Assert.Throws<RpcException>(() => service.Read(new ReadAssetExportRequest(first.Token, 1))).Code);
        now += AssetExportService.IdleTimeout;
        var third = service.Begin(new BeginAssetExportRequest("test"));
        Assert.True(streams[0].WasDisposed);
        Assert.True(streams[1].WasDisposed);
        Assert.Equal("asset_export_expired",
            Assert.Throws<RpcException>(() => service.Read(new ReadAssetExportRequest(second.Token, 0))).Code);
        var chunk = service.Read(new ReadAssetExportRequest(third.Token, 0));
        Assert.True(chunk.IsComplete);
        Assert.Equal(100, chunk.TotalBytes);
        Assert.NotNull(chunk.Sha256);
        Assert.Equal(0, service.ActiveCount);
        using var tooLarge = new AssetExportService(_ => new ReportedLengthStream(AssetExportMethods.MaximumBytes + 1));
        Assert.Equal("asset_export_limit",
            Assert.Throws<RpcException>(() => tooLarge.Begin(new BeginAssetExportRequest("test"))).Code);
        using var broken = new AssetExportService(_ => new BrokenSource());
        var brokenToken = broken.Begin(new BeginAssetExportRequest("test")).Token;
        Assert.Equal("asset_read_failed",
            Assert.Throws<RpcException>(() => broken.Read(new ReadAssetExportRequest(brokenToken, 0))).Code);
        Assert.Equal(0, broken.ActiveCount);
        var overflowing = new CountingNonSeekStream(AssetExportMethods.MaximumBytes + 1);
        using var limited = new AssetExportService(_ => overflowing);
        var limitedToken = limited.Begin(new BeginAssetExportRequest("test")).Token;
        long offset = 0;
        var exceeded = Assert.Throws<RpcException>((Action)(() =>
        {
            while (true)
            {
                var part = limited.Read(new ReadAssetExportRequest(limitedToken, offset));
                offset += part.Bytes.Length;
            }
        }));
        Assert.Equal("asset_export_limit", exceeded.Code);
        Assert.True(overflowing.WasDisposed);
        Assert.Equal(0, limited.ActiveCount);
    }

    private static DiagnosticsAgent Attach(Window window) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions
            { PipeName = "avdt-assets-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });

    private sealed class InlineProgress(Action<AssetExportProgress> report) : IProgress<AssetExportProgress>
    {
        public void Report(AssetExportProgress value) => report(value);
    }

    private sealed class FailingDestination : MemoryStream
    {
        public override ValueTask
            WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("destination unavailable");
    }

    private class ProbeStream(byte[] data) : MemoryStream(data)
    {
        public bool WasDisposed { get; private set; }
        public override bool CanSeek => false;

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ReportedLengthStream(long size) : MemoryStream
    {
        public override long Length => size;
    }

    private sealed class BrokenSource() : ProbeStream([])
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("read failed");
    }

    private sealed class CountingNonSeekStream(long remaining) : ProbeStream([])
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(remaining, count);
            buffer.AsSpan(offset, read).Clear();
            remaining -= read;
            return read;
        }
    }
}
