using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class HttpMcpIntegrationTests
{
    private static readonly string[] AgentToolNames =
    [
        "attach-to-app", "attach-to-file", "detach", "tree", "ancestors", "search", "props", "set-prop",
        "styles", "pseudo-class", "resources", "assets", "open-asset", "screenshot", "input", "action",
        "ui-input", "ui-click", "ui-key-chord", "ui-drag", "ui-text-selection", "ui-select-item",
        "ui-control-action", "ui-wait", "ui-screenshot", "native-windows", "native-tree", "native-find",
        "native-inspect", "native-action", "native-action-status", "native-input", "native-screenshot",
        "file-dialogs", "file-dialog-inspect", "file-dialog-set-paths", "file-dialog-set-filter",
        "file-dialog-accept", "file-dialog-cancel", "file-dialog-overwrite", "file-dialog-wait",
        "capture-start", "capture-status", "capture-stop"
    ];

    private static string CliPath => typeof(HttpMcpIntegrationTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(item => item.Key == "PreviewCliPath").Value!;

    [AvaloniaFact]
    public async Task Http_url_without_token_lists_agent_tools_and_keeps_real_target_state_across_requests()
    {
        using var timeout = TestTimeout();
        var token = timeout.Token;
        var text = new TextBlock { Name = "HttpTextTarget", Text = "http baseline", Width = 160 };
        var pixels = new Border { Name = "HttpPixelTarget", Width = 80, Height = 40, Background = Brushes.Blue };
        var window = new Window { Width = 240, Height = 180, Content = new StackPanel { Children = { text, pixels } } };
        window.Show();
        try
        {
            using (var frame = window.CaptureRenderedFrame())
                Assert.NotNull(frame);
            await using var agent = Attach(window);
            await using var host = await HttpHost.StartAsync(agent.PipeName, token);
            await using var connection = await HttpConnection.ConnectAsync(host.Endpoint, token);
            var client = connection.Client;

            var catalog = await client.ListToolsAsync(cancellationToken: token);
            Assert.Equal(AgentToolNames.Order(StringComparer.Ordinal),
                catalog.Select(tool => tool.Name).Order(StringComparer.Ordinal));
            Assert.False(string.IsNullOrWhiteSpace(client.SessionId));
            var sessionId = client.SessionId;
            await AttachAsync(client, agent.PipeName, token);

            var tree = Read<OfficialTreeSnapshot>(await Call(client, "tree", token, ("depth", -1)));
            var textId = Assert.Single(tree.Entries, entry => entry.Node.Name == text.Name).Node.Id;
            var pixelId = Assert.Single(tree.Entries, entry => entry.Node.Name == pixels.Name).Node.Id;
            Assert.Equal("http baseline", await TextPropertyAsync(client, textId, token));

            Success(await Call(client,
                "set-prop",
                token,
                ("nodeId", textId),
                ("propertyName", "Text"),
                ("value", "over HTTP")));
            Assert.Equal("over HTTP", text.Text);
            Assert.Equal("over HTTP", await TextPropertyAsync(client, textId, token));

            var screenshot = await Call(client, "ui-screenshot", token, ("nodeId", pixelId));
            Success(screenshot);
            var image = Assert.Single(screenshot.Content.OfType<ImageContentBlock>());
            Assert.Equal("image/png", image.MimeType);
            using (var bitmap = SKBitmap.Decode(image.DecodedData.ToArray()))
            {
                Assert.NotNull(bitmap);
                Assert.Equal(80, bitmap.Width);
                Assert.Equal(40, bitmap.Height);
                Assert.Equal(SKColors.Blue, bitmap.GetPixel(40, 20));
            }

            Assert.Equal(sessionId, client.SessionId);
            var subsequent = connection.Requests
                .Where(request => request.SessionId is not null && request.Method == "POST").ToArray();
            Assert.True(subsequent.Length >= 6);
            Assert.All(subsequent, request => Assert.Equal(sessionId, request.SessionId));
            Assert.All(connection.Requests, request => Assert.False(request.HasAuthorization));

            Success(await Call(client, "detach", token));
            await UntilAsync(() => !agent.IsConnected && text.Text == "http baseline",
                token,
                () => DescribeTarget(agent, text, "http baseline"));
            var detached = await Call(client, "props", token, ("nodeId", textId));
            Error(detached, "not_connected");
            Assert.Equal(sessionId, client.SessionId);
            Assert.Equal(44, (await client.ListToolsAsync(cancellationToken: token)).Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Http_control_is_claimed_by_attach_or_lazy_pipe_and_released_by_detach_and_session_delete()
    {
        using var timeout = TestTimeout();
        var token = timeout.Token;
        var text = new TextBlock { Name = "HttpOwnershipTarget", Text = "original", Width = 160 };
        var window = new Window { Width = 240, Height = 140, Content = text };
        window.Show();
        try
        {
            await using var agent = Attach(window);
            await using var host = await HttpHost.StartAsync(agent.PipeName, token);
            await using var first = await HttpConnection.ConnectAsync(host.Endpoint, token);
            await using var second = await HttpConnection.ConnectAsync(host.Endpoint, token);
            Assert.False(string.IsNullOrWhiteSpace(first.Client.SessionId));
            Assert.False(string.IsNullOrWhiteSpace(second.Client.SessionId));
            Assert.NotEqual(first.Client.SessionId, second.Client.SessionId);
            Assert.Equal(44, (await second.Client.ListToolsAsync(cancellationToken: token)).Count);
            var secondTargetId = await DiscoverAsync(second.Client, agent.PipeName, token);
            Assert.False(agent.IsConnected);

            // The configured pipe is attached lazily by the first target operation.
            var firstNode = await FindAsync(first.Client, text.Name!, token);
            Assert.Equal("original", await TextPropertyAsync(first.Client, firstNode, token));
            Success(await Call(first.Client,
                "set-prop",
                token,
                ("nodeId", firstNode),
                ("propertyName", "Text"),
                ("value", "first owner")));

            Error(await Call(second.Client, "attach-to-app", token, ("id", secondTargetId)), "controller_busy");
            Error(await Call(second.Client, "tree", token), "controller_busy");
            Assert.Equal("first owner", text.Text);
            Assert.Equal(44, (await second.Client.ListToolsAsync(cancellationToken: token)).Count);
            Assert.Equal(secondTargetId, await DiscoverAsync(second.Client, agent.PipeName, token));

            var firstSessionId = first.Client.SessionId;
            Success(await Call(first.Client, "detach", token));
            await UntilAsync(() => !agent.IsConnected && text.Text == "original",
                token,
                () => DescribeTarget(agent, text, "original"));
            Assert.Equal(firstSessionId, first.Client.SessionId);
            await AttachAsync(second.Client, agent.PipeName, token);
            var secondNode = await FindAsync(second.Client, text.Name!, token);
            Assert.NotEqual(firstNode, secondNode);
            Error(await Call(second.Client, "props", token, ("nodeId", firstNode)), "stale_node");
            Success(await Call(second.Client,
                "set-prop",
                token,
                ("nodeId", secondNode),
                ("propertyName", "Text"),
                ("value", "second owner")));
            Assert.Equal("second owner", text.Text);

            var secondSessionId = second.Client.SessionId;
            await second.DisposeAsync();
            await UntilAsync(() => !agent.IsConnected && text.Text == "original",
                token,
                () => DescribeTarget(agent, text, "original"));
            Assert.Contains(second.Requests,
                request => request.Method == "DELETE" && request.SessionId == secondSessionId);

            // The original HTTP session remains usable after another session owned the target.
            await AttachAsync(first.Client, agent.PipeName, token);
            var reattachedNode = await FindAsync(first.Client, text.Name!, token);
            Assert.NotEqual(firstNode, reattachedNode);
            Assert.NotEqual(secondNode, reattachedNode);
            Error(await Call(first.Client, "props", token, ("nodeId", secondNode)), "stale_node");
            Success(await Call(first.Client,
                "set-prop",
                token,
                ("nodeId", reattachedNode),
                ("propertyName", "Text"),
                ("value", "last override")));
            await first.DisposeAsync();
            await UntilAsync(() => !agent.IsConnected && text.Text == "original",
                token,
                () => DescribeTarget(agent, text, "original"));

            await using var next = await HttpConnection.ConnectAsync(host.Endpoint, token);
            await AttachAsync(next.Client, agent.PipeName, token);
            Assert.Equal("original",
                await TextPropertyAsync(next.Client, await FindAsync(next.Client, text.Name!, token), token));
        }
        finally
        {
            window.Close();
        }
    }

    [Theory]
    [InlineData("official", 16)]
    [InlineData("extended", 62)]
    public async Task Historical_profiles_remain_stdio_only(string profile, int expectedTools)
    {
        using var timeout = TestTimeout();
        var token = timeout.Token;
        await using (var stdio = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
                         {
                             Command = "dotnet", Arguments = [CliPath, "mcp", "--profile", profile],
                             Name = "HTTP profile boundary", ShutdownTimeout = TimeSpan.FromSeconds(5)
                         }),
                         cancellationToken: token))
            Assert.Equal(expectedTools, (await stdio.ListToolsAsync(cancellationToken: token)).Count);

        await using var rejected = HttpHost.StartProcess(null, profile);
        await rejected.Process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
        Assert.Equal(2, rejected.Process.ExitCode);
        Assert.Contains("profile", (await rejected.Errors).ToLowerInvariant());
    }

    private static CancellationTokenSource TestTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        return timeout;
    }

    private static DiagnosticsAgent Attach(Window window) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions
        {
            ApplicationName = "HTTP MCP integration",
            PipeName = DiagnosticsProtocol.PipePrefix + "http-test-" + Guid.NewGuid().ToString("N"),
            TopLevelProvider = () => [window]
        });

    private static async Task<long> DiscoverAsync(McpClient client, string pipe, CancellationToken token)
    {
        var listed = Read<OfficialAttachmentResult>(await Call(client, "attach-to-app", token));
        Assert.False(listed.Attached);
        return Assert.Single(listed.Applications, application => application.PipeName == pipe).Id;
    }

    private static async Task AttachAsync(McpClient client, string pipe, CancellationToken token)
    {
        var id = await DiscoverAsync(client, pipe, token);
        var attached = Read<OfficialAttachmentResult>(await Call(client, "attach-to-app", token, ("id", id)));
        Assert.True(attached.Attached);
        Assert.Equal(Environment.ProcessId, attached.Session!.ProcessId);
    }

    private static async Task<long> FindAsync(McpClient client, string name, CancellationToken token) =>
        Assert.Single(Read<TreeSearchResult>(await Call(client, "search", token, ("query", name))).Matches,
            node => node.Name == name).Id;

    private static async Task<string?> TextPropertyAsync(McpClient client, long nodeId, CancellationToken token) =>
        Assert.Single(Read<PropertyInfoDto[]>(await Call(client,
            "props",
            token,
            ("nodeId", nodeId),
            ("propertyNames", new[] { "Text" })))).Value;

    private static ValueTask<CallToolResult> Call(McpClient client,
        string tool,
        CancellationToken token,
        params (string Name, object? Value)[] arguments) => client.CallToolAsync(tool,
        arguments.ToDictionary(argument => argument.Name, argument => argument.Value),
        cancellationToken: token);

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

    private static void Success(CallToolResult result) => Assert.NotEqual(true, result.IsError);

    private static void Error(CallToolResult result, string code)
    {
        Assert.True(result.IsError, Text(result));
        Assert.Contains(code, Text(result));
    }

    private static string Text(CallToolResult result) => string.Join("\n",
        result.Content.OfType<TextContentBlock>().Select(content => content.Text));

    private static string DescribeTarget(DiagnosticsAgent agent, TextBlock text, string expected) =>
        $"AgentConnected={agent.IsConnected}; Expected={expected}; Actual={text.Text}; " +
        $"Priority={text.GetDiagnostic(TextBlock.TextProperty).Priority}; BaseValue={text.GetBaseValue(TextBlock.TextProperty)}; " +
        $"AgentLastError={agent.LastError ?? "<none>"}";

    private static async Task UntilAsync(Func<bool> predicate, CancellationToken token, Func<string> describe)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            while (!predicate())
                await Task.Delay(25, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The HTTP target did not finish cleanup. " + describe());
        }
    }

    internal sealed record HttpRequestObservation(string Method, string? SessionId, bool HasAuthorization);

    internal sealed class RecordingHttpHandler : DelegatingHandler
    {
        public ConcurrentQueue<HttpRequestObservation> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue(new(request.Method.Method,
                request.Headers.TryGetValues("Mcp-Session-Id", out var values) ? values.SingleOrDefault() : null,
                request.Headers.Contains("Authorization")));
            return base.SendAsync(request, cancellationToken);
        }
    }

    internal sealed class HttpConnection(McpClient client, RecordingHttpHandler recorder) : IAsyncDisposable
    {
        private int _disposed;
        public McpClient Client { get; } = client;
        public IEnumerable<HttpRequestObservation> Requests => recorder.Requests;

        public static async Task<HttpConnection> ConnectAsync(Uri endpoint, CancellationToken token)
        {
            var recorder = new RecordingHttpHandler { InnerHandler = new SocketsHttpHandler { UseProxy = false } };
            var http = new HttpClient(recorder) { Timeout = TimeSpan.FromSeconds(20) };
            var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = endpoint, TransportMode = HttpTransportMode.StreamableHttp,
                    EnableStandaloneGetStream = false, OwnsSession = true, Name = "HTTP integration without credentials"
                },
                http,
                loggerFactory: null,
                ownsHttpClient: true);
            try
            {
                return new(await McpClient.CreateAsync(transport, cancellationToken: token), recorder);
            }
            catch
            {
                await transport.DisposeAsync();
                http.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await Client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    internal sealed class HttpHost : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly Task<string> _output;
        private int _disposed;
        public Process Process { get; }
        public Task<string> Errors { get; }
        public Uri Endpoint { get; }

        private HttpHost(Process process, int port, string directory)
        {
            Process = process;
            Endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            _directory = directory;
            _output = process.StandardOutput.ReadToEndAsync();
            Errors = process.StandardError.ReadToEndAsync();
        }

        public static HttpHost StartProcess(string? pipe,
            string? profile = null,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            int port;
            try
            {
                listener.Start();
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }

            var directory = Path.Combine(Path.GetTempPath(), "avdt-http-mcp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                foreach (var argument in new[]
                         {
                             CliPath, "mcp", "--transport", "http", "--listen", "127.0.0.1", "--port",
                             port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                         })
                    start.ArgumentList.Add(argument);
                if (pipe is not null)
                {
                    start.ArgumentList.Add("--pipe");
                    start.ArgumentList.Add(pipe);
                }

                if (profile is not null)
                {
                    start.ArgumentList.Add("--profile");
                    start.ArgumentList.Add(profile);
                }

                if (environment is not null)
                    foreach (var pair in environment)
                        start.Environment[pair.Key] = pair.Value;
                return new(Process.Start(start) ??
                           throw new InvalidOperationException("The HTTP CLI process did not start."),
                    port,
                    directory);
            }
            catch
            {
                DeleteTemporaryDirectory(directory);
                throw;
            }
        }

        public static async Task<HttpHost> StartAsync(string? pipe,
            CancellationToken token,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            var host = StartProcess(pipe, environment: environment);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                while (true)
                {
                    if (host.Process.HasExited)
                        throw new InvalidOperationException("The HTTP CLI exited before listening: " +
                                                            await host.Errors + await host._output);
                    try
                    {
                        using var socket = new TcpClient();
                        await socket.ConnectAsync(IPAddress.Loopback, host.Endpoint.Port, timeout.Token);
                        return host;
                    }
                    catch (SocketException)
                    {
                        await Task.Delay(30, timeout.Token);
                    }
                }
            }
            catch
            {
                await host.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                if (!Process.HasExited)
                    Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                await Task.WhenAll(_output, Errors).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Process.Dispose();
                DeleteTemporaryDirectory(_directory);
            }
        }

        private static void DeleteTemporaryDirectory(string directory)
        {
            var root = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var target = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(target).StartsWith("avdt-http-mcp-", StringComparison.Ordinal))
                throw new InvalidOperationException("HTTP test cleanup escaped its temporary directory.");
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
    }
}
