using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaDevTool.Protocol;
using AvaloniaDevTool.Recording;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using HttpHost = AvaloniaDevTool.Cli.Tests.HttpMcpIntegrationTests.HttpHost;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class RecordingExportTests
{
    [Fact]
    public async Task Serve_chunks_keep_raw_bytes_and_base64_JSON_below_the_large_object_threshold()
    {
        using var fixture = await ArtifactFixture.CreateAsync();
        await using var host = await HttpHost.StartAsync(null, Token);
        await using var connection = await HttpMcpIntegrationTests.HttpConnection.ConnectAsync(host.Endpoint, Token);
        var result = await Call(connection.Client, "recording-export", ("artifactId", fixture.RecordingId));
        Assert.False(result.IsError == true, Text(result));
        var text = Text(result);
        Assert.InRange(text.Length, 1, 40_000); // UTF-16 text plus its header remains below 85,000 bytes.
        using var metadata = JsonDocument.Parse(text);
        var bytes = metadata.RootElement.GetProperty("Bytes").GetBytesFromBase64();
        Assert.InRange(bytes.Length, 1, 16 * 1024);
        Assert.Equal(fixture.Bytes.AsSpan(0, bytes.Length).ToArray(), bytes);
        var tooLarge = await Call(connection.Client, "recording-export",
            ("artifactId", fixture.RecordingId), ("maxBytes", 262_144));
        Assert.True(tooLarge.IsError == true, Text(tooLarge));
        Assert.Contains("invalid_argument", Text(tooLarge));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stdio_gateway_lists_without_attachment_and_exports_multiple_verified_chunks(bool throughHttp)
    {
        using var fixture = await ArtifactFixture.CreateAsync();
        await using var host = throughHttp ? await HttpHost.StartAsync(null, Token) : null;
        await using var client = await CreateClientAsync(host?.Endpoint.Port);
        if (host is not null)
        {
            var selected = await Call(client, "select-machine", ("ip", "127.0.0.1"));
            Assert.False(selected.IsError == true, Text(selected));
        }

        var listed = Read<RecordingStatus[]>(await Call(client, "recording-list", ("limit", 1000)));
        Assert.Contains(listed, item => item.RecordingId == fixture.RecordingId && item.State == "completed");
        Assert.True(fixture.Bytes.Length > 2 * 262_144);

        var result = await Call(client, "recording-export",
            ("artifactId", fixture.RecordingId), ("destinationPath", fixture.DestinationPath));

        Assert.False(result.IsError == true, Text(result));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.DestinationPath, Token));
        using var metadata = JsonDocument.Parse(Text(result));
        Assert.Equal(fixture.DestinationPath, metadata.RootElement.GetProperty("Path").GetString());
        Assert.Equal(fixture.Bytes.LongLength, metadata.RootElement.GetProperty("ByteLength").GetInt64());
        Assert.Equal(fixture.Sha256, metadata.RootElement.GetProperty("Sha256").GetString());
        Assert.True(metadata.RootElement.GetProperty("Complete").GetBoolean());
        Assert.Empty(Directory.EnumerateFiles(fixture.DownloadDirectory, ".avdt-recording-*.tmp"));
    }

    [Fact]
    public async Task Stdio_gateway_rejects_wrong_hash_without_committing_or_leaving_temporary_files()
    {
        using var fixture = await ArtifactFixture.CreateAsync(wrongHash: true);
        await using var client = await CreateClientAsync();

        var result = await Call(client, "recording-export",
            ("artifactId", fixture.RecordingId), ("destinationPath", fixture.DestinationPath));

        Assert.True(result.IsError, Text(result));
        Assert.Contains("recording_integrity_failed", Text(result));
        Assert.False(File.Exists(fixture.DestinationPath));
        Assert.Empty(Directory.EnumerateFiles(fixture.DownloadDirectory, ".avdt-recording-*.tmp"));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.SourcePath, Token));
    }

    [Fact]
    public async Task Stdio_gateway_does_not_overwrite_an_existing_destination()
    {
        using var fixture = await ArtifactFixture.CreateAsync();
        byte[] existing = [1, 7, 42, 99];
        await File.WriteAllBytesAsync(fixture.DestinationPath, existing, Token);
        await using var client = await CreateClientAsync();

        var result = await Call(client, "recording-export",
            ("artifactId", fixture.RecordingId), ("destinationPath", fixture.DestinationPath));

        Assert.True(result.IsError, Text(result));
        Assert.Contains("file_exists", Text(result));
        Assert.Equal(existing, await File.ReadAllBytesAsync(fixture.DestinationPath, Token));
        Assert.Empty(Directory.EnumerateFiles(fixture.DownloadDirectory, ".avdt-recording-*.tmp"));
        Assert.Equal(fixture.Bytes, await File.ReadAllBytesAsync(fixture.SourcePath, Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<McpClient> CreateClientAsync(int? executorPort = null)
    {
        var executable = Environment.GetEnvironmentVariable("AVDT_TEST_CLI_EXECUTABLE");
        var cliPath = typeof(RecordingExportTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(item => item.Key == "PreviewCliPath").Value!;
        var options = new StdioClientTransportOptions
        {
            Command = executable ?? "dotnet", Name = "Recording export integration",
            Arguments = executable is null ? [cliPath, "mcp"] : ["mcp"],
            EnvironmentVariables = executorPort is { } port
                ? new Dictionary<string, string?> { ["AVDT_EXECUTOR_PORT"] = port.ToString(CultureInfo.InvariantCulture) }
                : null,
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        };
        return McpClient.CreateAsync(new StdioClientTransport(options), cancellationToken: Token);
    }

    private static ValueTask<CallToolResult> Call(McpClient client, string name,
        params (string Name, object? Value)[] arguments) => client.CallToolAsync(name,
        arguments.ToDictionary(item => item.Name, item => item.Value), cancellationToken: Token);

    private static T Read<T>(CallToolResult result)
    {
        Assert.False(result.IsError == true, Text(result));
        var root = result.StructuredContent is { } content
            ? JsonNode.Parse(JsonSerializer.Serialize(content))
            : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private sealed class ArtifactFixture : IDisposable
    {
        private const string Prefix = "avdt-recording-export-";
        private readonly string _root;
        private readonly string _indexPath;

        private ArtifactFixture(string id, string root, string indexPath, byte[] bytes)
        {
            RecordingId = id;
            _root = root;
            _indexPath = indexPath;
            Bytes = bytes;
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        public string RecordingId { get; }
        public byte[] Bytes { get; }
        public string Sha256 { get; }
        public string SourcePath => Path.Combine(_root, "source.mp4");
        public string DownloadDirectory => Path.Combine(_root, "download");
        public string DestinationPath => Path.Combine(DownloadDirectory, "exported.mp4");

        public static async Task<ArtifactFixture> CreateAsync(bool wrongHash = false)
        {
            var id = "recording-" + Guid.NewGuid().ToString("N");
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N")));
            var indexDirectory = Path.Combine(RecordingArtifacts.DefaultDirectory, "index");
            var fixture = new ArtifactFixture(id, root, Path.Combine(indexDirectory, id + ".json"),
                RandomNumberGenerator.GetBytes(700_013));
            try
            {
                Directory.CreateDirectory(fixture.DownloadDirectory);
                Directory.CreateDirectory(indexDirectory);
                await File.WriteAllBytesAsync(fixture.SourcePath, fixture.Bytes, Token);
                var status = new RecordingStatus
                {
                    RecordingId = id, ArtifactId = id, OutputPath = fixture.SourcePath, State = "completed",
                    BytesWritten = fixture.Bytes.LongLength,
                    Sha256 = wrongHash ? new string('0', 64) : fixture.Sha256,
                    StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1), StoppedAtUtc = DateTimeOffset.UtcNow
                };
                await File.WriteAllTextAsync(fixture._indexPath, JsonSerializer.Serialize(status, ProtocolJson.Options), Token);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            // Delete only this fixture's GUID index, never the shared index directory.
            var expectedIndex = Path.GetFullPath(Path.Combine(RecordingArtifacts.DefaultDirectory, "index",
                RecordingId + ".json"));
            if (!string.Equals(Path.GetFullPath(_indexPath), expectedIndex, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Recording export cleanup escaped its index file.");
            if (File.Exists(_indexPath))
                File.Delete(_indexPath);

            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));
            if (!string.Equals(Path.GetDirectoryName(target), temporaryRoot, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).StartsWith(Prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Recording export cleanup escaped its temporary directory.");
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }
}
