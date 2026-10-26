using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using Xunit;
using HttpConnection = AvaloniaDevTool.Cli.Tests.HttpMcpIntegrationTests.HttpConnection;
using HttpHost = AvaloniaDevTool.Cli.Tests.HttpMcpIntegrationTests.HttpHost;
using ProjectFiles = AvaloniaDevTool.Cli.Tests.OfficialPreviewTests.ProjectFiles;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class HttpPreviewMcpTests
{
    [Fact]
    public async Task
        Http_preview_loads_real_project_resources_reloads_nodes_and_reclaims_children_on_detach_and_delete()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));
        var token = timeout.Token;
        using var files = new ProjectFiles();
        var initial = files.Initial.Replace("<referenced:ReferencedBadge />",
            "<referenced:ReferencedBadge /><referenced:ReferencedView />",
            StringComparison.Ordinal);
        Assert.NotEqual(files.Initial, initial);
        files.Write(initial);
        await files.BuildAsync();
        var assemblyWritten = File.GetLastWriteTimeUtc(files.Output);
        await using var host = await HttpHost.StartAsync(null,
            token,
            new Dictionary<string, string> { ["AVDT_PREVIEW_HEADLESS"] = "true" });
        HttpConnection? connection = null;
        var children = new List<OwnedPreview>();
        try
        {
            connection = await HttpConnection.ConnectAsync(host.Endpoint, token);
            var client = connection.Client;
            var sessionId = client.SessionId;
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            var first = Read<PreviewAttachment>(await Call(client, "attach-to-file", files.Arguments(), token));
            var firstChild = OwnedPreview.Capture(first.Session.ProcessId);
            children.Add(firstChild);
            Assert.True(first.Headless);
            Assert.True(first.Watching);
            Assert.Equal(files.Source, first.FilePath);
            Assert.Equal(files.Project, first.ProjectPath);
            Assert.Equal(files.Output, first.AssemblyPath);
            Assert.Equal(assemblyWritten, File.GetLastWriteTimeUtc(files.Output));
            Assert.False(File.Exists(files.BuildMarker));
            Assert.Equal("Project App resource", (await Find(client, "ProjectResourceText", token)).Preview);
            Assert.Equal("Built custom control", (await Find(client, "CustomControlText", token)).Preview);
            Assert.Equal("Referenced assembly control", (await Find(client, "ReferencedControlText", token)).Preview);
            Assert.Equal("Project App resource", (await Find(client, "LibraryAppResource", token)).Preview);
            var originalText = await Find(client, "SavedText", token);
            Assert.Equal("First source", originalText.Preview);
            var originalView = await FindType(client, "AvaloniaDevTool.PreviewFixture.FixtureView", token);
            var originalPng = await Screenshot(client, originalView.Id, token);

            const string updatedText = "HTTP 保存后重新加载 中文";
            files.Write(initial.Replace("First source", updatedText, StringComparison.Ordinal));
            var reloaded = await WaitText(client, "SavedText", updatedText, token);
            Assert.NotEqual(originalText.Id, reloaded.Id);
            var stale = await Call(client, "props", new() { ["nodeId"] = originalText.Id }, token);
            Assert.True(stale.IsError, Text(stale));
            Assert.Contains("stale_node", Text(stale));
            var updatedView = await FindType(client, "AvaloniaDevTool.PreviewFixture.FixtureView", token);
            Assert.NotEqual(originalPng, await Screenshot(client, updatedView.Id, token));
            Assert.Equal(sessionId, client.SessionId);
            Assert.Equal(assemblyWritten, File.GetLastWriteTimeUtc(files.Output));

            Success(await Call(client, "detach", new(), token));
            await firstChild.WaitForExitAsync(token);
            var detached = await Call(client, "tree", new(), token);
            Assert.True(detached.IsError, Text(detached));
            Assert.Contains("not_connected", Text(detached));

            var second = Read<PreviewAttachment>(await Call(client, "attach-to-file", files.Arguments(), token));
            var secondChild = OwnedPreview.Capture(second.Session.ProcessId);
            children.Add(secondChild);
            Assert.True(second.Headless);
            Assert.NotEqual(first.Session.ProcessId, second.Session.ProcessId);
            Assert.NotEqual(first.Session.SessionId, second.Session.SessionId);
            Assert.Equal(updatedText, (await Find(client, "SavedText", token)).Preview);
            Assert.Equal(sessionId, client.SessionId);
            Assert.All(connection.Requests, request => Assert.False(request.HasAuthorization));

            await connection.DisposeAsync();
            Assert.Contains(connection.Requests,
                request => request.Method == "DELETE" && request.SessionId == sessionId);
            await secondChild.WaitForExitAsync(token);
        }
        finally
        {
            try
            {
                if (connection is not null)
                    await connection.DisposeAsync();
            }
            finally
            {
                await Task.WhenAll(children.Select(child => child.DisposeAsync().AsTask()));
            }
        }
    }

    private static async Task<CallToolResult> Call(McpClient client,
        string tool,
        Dictionary<string, object?> arguments,
        CancellationToken token) =>
        await client.CallToolAsync(tool, arguments, cancellationToken: token).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(35), token);

    private static async Task<TreeNodeInfo> Find(McpClient client, string name, CancellationToken token) =>
        Assert.Single(Read<TreeSearchResult>(await Call(client, "search", new() { ["query"] = name }, token)).Matches,
            node => node.Name == name);

    private static async Task<TreeNodeInfo> FindType(McpClient client, string type, CancellationToken token) =>
        Assert.Single(Read<TreeSearchResult>(await Call(client, "search", new() { ["query"] = type }, token)).Matches,
            node => node.TypeName == type);

    private static async Task<TreeNodeInfo> WaitText(McpClient client,
        string name,
        string expected,
        CancellationToken token)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        wait.CancelAfter(TimeSpan.FromSeconds(15));
        string? observed = null;
        try
        {
            while (true)
            {
                var response = await Call(client, "search", new() { ["query"] = name }, wait.Token);
                if (response.IsError == true && Text(response).Contains("stale_session", StringComparison.Ordinal))
                {
                    await Task.Delay(40, wait.Token);
                    continue;
                }

                var node = Read<TreeSearchResult>(response).Matches.SingleOrDefault(value => value.Name == name);
                observed = node?.Preview;
                if (node is not null && observed == expected)
                    return node;
                await Task.Delay(40, wait.Token);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The HTTP preview did not reload {name}. Expected={expected}; Actual={observed ?? "<absent>"}.");
        }
    }

    private static async Task<string> Screenshot(McpClient client, long nodeId, CancellationToken token)
    {
        var response = await Call(client, "screenshot", new() { ["nodeId"] = nodeId }, token);
        Success(response);
        var image = Assert.Single(response.Content.OfType<ImageContentBlock>());
        Assert.Equal("image/png", image.MimeType);
        using var bitmap = SKBitmap.Decode(image.DecodedData.ToArray());
        Assert.NotNull(bitmap);
        Assert.True(bitmap.Width > 1 && bitmap.Height > 1);
        Assert.True(bitmap.Pixels.Distinct().Take(3).Count() >= 3,
            "The HTTP preview must return actual multicolor rendered pixels.");
        return Convert.ToBase64String(image.DecodedData.Span);
    }

    private static string Text(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(item => item.Text));

    private static void Success(CallToolResult result) => Assert.False(result.IsError == true, Text(result));

    private static T Read<T>(CallToolResult result)
    {
        Success(result);
        var root = result.StructuredContent is { } content
            ? JsonNode.Parse(JsonSerializer.Serialize(content))
            : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }

    private sealed class OwnedPreview : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly DateTime _startedUtc;

        private OwnedPreview(Process process)
        {
            _process = process;
            _startedUtc = process.StartTime.ToUniversalTime();
        }

        public static OwnedPreview Capture(int processId)
        {
            var process = Process.GetProcessById(processId);
            try
            {
                Assert.False(process.HasExited, "The newly attached HTTP preview already exited.");
                return new(process);
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

        public async Task WaitForExitAsync(CancellationToken token) =>
            await _process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(10), token);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_process.HasExited)
                    return;
                if (_process.StartTime.ToUniversalTime() != _startedUtc)
                    throw new InvalidOperationException(
                        "The owned HTTP preview process identity changed; refusing to terminate it.");
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
