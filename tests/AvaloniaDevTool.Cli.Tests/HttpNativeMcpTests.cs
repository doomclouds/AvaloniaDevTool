using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SkiaSharp;
using Xunit;
using HttpConnection = AvaloniaDevTool.Cli.Tests.HttpMcpIntegrationTests.HttpConnection;
using HttpHost = AvaloniaDevTool.Cli.Tests.HttpMcpIntegrationTests.HttpHost;

namespace AvaloniaDevTool.Cli.Tests;

[Collection(NativeDesktopMcpCollection.Name)]
public sealed class HttpNativeMcpTests
{
    private const string MainTitle = "AvaloniaDevTool automation acceptance";
    private const string GateDialogTitle = "Automation sample - Managed gate";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static readonly JsonSerializerOptions WireOptions = new(ProtocolJson.Options)
        { Converters = { new JsonStringEnumConverter() } };

    public static bool IsInteractiveWindows => OperatingSystem.IsWindows() && Environment.UserInteractive;

    [Fact(SkipUnless = nameof(IsInteractiveWindows),
        Skip = "A real interactive Windows desktop is required for HTTP native/modal acceptance.")]
    public async Task Http_session_answers_owned_native_modal_during_pending_managed_calls_and_roundtrips_utf8_files()
    {
        using var calls = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        calls.CancelAfter(TimeSpan.FromSeconds(180));
        var token = calls.Token;
        await using var sample = await SampleProcess.StartAsync(token);
        await using var host = await HttpHost.StartAsync(sample.PipeName, token);
        HttpConnection? connection = null;
        var pending = new List<Task<CallToolResult>>();
        try
        {
            connection = await HttpConnection.ConnectAsync(host.Endpoint, token);
            var client = connection.Client;
            var sessionId = client.SessionId;
            Assert.False(string.IsNullOrWhiteSpace(sessionId));
            var discovered = Read<ApplicationAttachmentResult>(await Call(client, "attach-to-app", token));
            var target = Assert.Single(discovered.Applications, application => application.PipeName == sample.PipeName);
            var attached =
                Read<ApplicationAttachmentResult>(await Call(client, "attach-to-app", token, ("id", target.Id)));
            Assert.True(attached.Attached);
            Assert.Equal(sample.ProcessId, attached.Session!.ProcessId);

            var button = await FindManaged(client, "BlockManagedGateButton", token);
            var status = await FindManaged(client, "ManagedGateStatus", token);
            Assert.Equal("Ready", await ReadText(client, status, token));
            Assert.True(Read<ElementActionResult>(await Call(client,
                "action",
                token,
                ("nodeId", button),
                ("action", "BringIntoView"))).Succeeded);
            var main = Assert.Single(Read<NativeWindowInfo[]>(await Call(client, "native-windows", token)),
                window => window.ProcessId == sample.ProcessId && window.Title == MainTitle);
            Assert.False(main.IsOwned);

            // The actual managed RPC stays inside synchronous MessageBoxW; the queued read uses the same MCP session.
            var click = StartCall(client, "ui-click", token, ("nodeId", button));
            pending.Add(click);
            var dialog = await WaitWindow(client, sample.ProcessId, GateDialogTitle, token);
            Assert.True(dialog.IsOwned);
            Assert.Equal(main.Handle, dialog.OwnerHandle);
            Assert.Equal("#32770", dialog.ClassName);
            Assert.False(click.IsCompleted, "The real managed click must still be waiting for its native modal.");
            var queuedRead = StartCall(client, "props", token, ("nodeId", status), ("propertyNames", new[] { "Text" }));
            pending.Add(queuedRead);
            await Task.Delay(100, token);
            Assert.False(click.IsCompleted);
            Assert.False(queuedRead.IsCompleted,
                "The managed read unexpectedly passed the occupied managed call gate.");

            var during = Read<NativeWindowInfo[]>(await Call(client, "native-windows", token));
            Assert.All(during, window => Assert.Equal(sample.ProcessId, window.ProcessId));
            Assert.False(Assert.Single(during, window => window.Handle == main.Handle).IsEnabled);
            Assert.Contains(during, window => window.Handle == dialog.Handle && window.OwnerHandle == main.Handle);
            var buttons = Read<NativeFindResult>(await Call(client,
                "native-find",
                token,
                ("rootNodeId", dialog.NodeId),
                ("className", "Button"),
                ("controlType", "Button"),
                ("maxDepth", 4),
                ("maxNodes", 100)));
            Assert.False(buttons.Truncated);
            var ok = Assert.Single(buttons.Matches);
            Assert.Contains(ok.AutomationId, new[] { "1", "2" });
            Assert.Equal(sample.ProcessId, ok.ProcessId);
            Assert.Contains("Invoke", ok.SupportedPatterns);
            Assert.True(ok.IsEnabled);
            Assert.False(click.IsCompleted);
            Assert.False(queuedRead.IsCompleted);
            var answer = Read<NativeActionResult>(await Call(client,
                "native-action",
                token,
                ("nodeId", ok.NodeId),
                ("action", "Invoke")));
            Assert.Equal("dispatched", answer.Status);
            Assert.True(Read<InputResult>(await click.WaitAsync(TimeSpan.FromSeconds(10), token)).Delivered);
            Assert.Equal("Returned", ReadText(await queuedRead.WaitAsync(TimeSpan.FromSeconds(10), token)));
            var after = Read<NativeWindowInfo[]>(await Call(client, "native-windows", token));
            Assert.DoesNotContain(after, window => window.Handle == dialog.Handle || window.Title == GateDialogTitle);
            Assert.True(Assert.Single(after, window => window.Handle == main.Handle).IsEnabled);

            Read<ItemSelectionResult>(await Call(client,
                "ui-select-item",
                token,
                ("nodeId", await FindManaged(client, "MainTabs", token)),
                ("index", 1)));
            var documentId = await FindManaged(client, "DocumentEditor", token);
            const string content = "HTTP 原生文件闭环 😀\n第二行：空格与 UTF-8。\n";
            var sourcePath = Path.Combine(sample.DirectoryPath, "输入 中文.txt");
            var savedPath = Path.Combine(sample.DirectoryPath, "保存 HTTP 文档.txt");
            var expectedBytes = Utf8.GetBytes(content);
            await File.WriteAllBytesAsync(sourcePath, expectedBytes, token);

            await ChooseFile(client,
                sample,
                "OpenSingleButton",
                "Automation sample - Open single",
                NativeFileDialogKind.OpenFile,
                sourcePath,
                main.Handle,
                pending,
                token);
            var opened = await sample.WaitResultAsync(0, "openSingle", token);
            Assert.Equal(new[] { sourcePath }, opened.SelectedPaths);
            Assert.Equal(content, opened.Content);
            Assert.Equal(content, await ReadText(client, documentId, token));

            await ChooseFile(client,
                sample,
                "SaveAsButton",
                "Automation sample - Save UTF-8 document",
                NativeFileDialogKind.SaveFile,
                savedPath,
                main.Handle,
                pending,
                token);
            var saved = await sample.WaitResultAsync(opened.Sequence, "saveAs", token);
            Assert.Equal(savedPath, saved.DocumentPath);
            var actualBytes = await File.ReadAllBytesAsync(savedPath, token);
            Assert.Equal(expectedBytes, actualBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(actualBytes)), saved.Sha256, ignoreCase: true);
            Assert.Equal(actualBytes.Length, saved.ByteLength);

            await ChooseFile(client,
                sample,
                "OpenSingleButton",
                "Automation sample - Open single",
                NativeFileDialogKind.OpenFile,
                savedPath,
                main.Handle,
                pending,
                token);
            var reopened = await sample.WaitResultAsync(saved.Sequence, "openSingle", token);
            Assert.Equal(content, reopened.Content);
            Assert.Equal(savedPath, reopened.DocumentPath);
            Assert.Equal(content, await ReadText(client, documentId, token));
            var screenshot = await Call(client, "ui-screenshot", token, ("nodeId", documentId));
            Assert.False(screenshot.IsError == true, Text(screenshot));
            var image = Assert.Single(screenshot.Content.OfType<ImageContentBlock>());
            Assert.Equal("image/png", image.MimeType);
            using (var bitmap = SKBitmap.Decode(image.DecodedData.ToArray()))
            {
                Assert.NotNull(bitmap);
                Assert.True(bitmap.Width > 0 && bitmap.Height > 0);
            }

            Assert.Equal(sessionId, client.SessionId);
            Assert.All(connection.Requests, request => Assert.False(request.HasAuthorization));
            Evidence("HTTP native/modal and real file roundtrip",
                new
                {
                    sample.ProcessId, SessionId = sessionId, ModalAnswered = true, Bytes = actualBytes.Length,
                    Sha256 = saved.Sha256, Reopened = true, ImageMimeType = image.MimeType
                });
        }
        finally
        {
            // Stop only this owned target so a failed assertion cannot leave its real modal waiting on the desktop.
            calls.Cancel();
            try
            {
                await sample.StopAsync();
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
                    foreach (var request in pending)
                        await ObserveCompletion(request);
                }
            }
        }
    }

    private static async Task ChooseFile(McpClient client,
        SampleProcess sample,
        string button,
        string title,
        NativeFileDialogKind kind,
        string path,
        long owner,
        List<Task<CallToolResult>> pending,
        CancellationToken token)
    {
        var nodeId = await FindManaged(client, button, token);
        Assert.True(Read<ElementActionResult>(await Call(client,
            "action",
            token,
            ("nodeId", nodeId),
            ("action", "BringIntoView"))).Succeeded);
        var click = StartCall(client, "ui-click", token, ("nodeId", nodeId));
        pending.Add(click);
        var dialog = Read<NativeFileDialogInfo>(await Call(client, "file-dialog-wait", token, ("timeoutMs", 10000)));
        Assert.Equal(sample.ProcessId, dialog.Window.ProcessId);
        Assert.Equal(title, dialog.Window.Title);
        Assert.True(dialog.Window.IsOwned);
        Assert.Equal(owner, dialog.Window.OwnerHandle);
        Assert.False(dialog.ApplicationIoVerified);
        Assert.Contains(Read<NativeFileDialogInfo[]>(await Call(client, "file-dialogs", token)),
            item => item.DialogId == dialog.DialogId);
        var entered = Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-set-paths",
            token,
            ("dialogId", dialog.DialogId),
            ("paths", new[] { path }),
            ("kind", kind.ToString())));
        Assert.Equal(new[] { path }, entered.RequestedPaths);
        Assert.False(entered.ApplicationIoVerified);
        Read<NativeFileDialogInfo>(await Call(client, "file-dialog-accept", token, ("dialogId", dialog.DialogId)));
        var closed = Read<NativeFileDialogInfo>(await Call(client,
            "file-dialog-wait",
            token,
            ("dialogId", dialog.DialogId),
            ("timeoutMs", 10000)));
        Assert.EndsWith("Closed", closed.State);
        Assert.Empty(closed.Prompts);
        Assert.False(closed.ApplicationIoVerified);
        Assert.True(Read<InputResult>(await click.WaitAsync(TimeSpan.FromSeconds(10), token)).Delivered);
    }

    private static async Task<NativeWindowInfo> WaitWindow(McpClient client,
        int processId,
        string title,
        CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var windows = Read<NativeWindowInfo[]>(await Call(client, "native-windows", token));
            Assert.All(windows, window => Assert.Equal(processId, window.ProcessId));
            if (windows.SingleOrDefault(window => window.Title == title) is { } found)
                return found;
            await Task.Delay(40, token);
        }

        throw new TimeoutException($"The HTTP client did not observe the owned modal '{title}'.");
    }

    private static Task<CallToolResult> StartCall(McpClient client,
        string tool,
        CancellationToken token,
        params (string Name, object? Value)[] arguments) => client.CallToolAsync(tool,
        arguments.ToDictionary(argument => argument.Name, argument => argument.Value),
        cancellationToken: token).AsTask();

    private static async Task<CallToolResult> Call(McpClient client,
        string tool,
        CancellationToken token,
        params (string Name, object? Value)[] arguments) =>
        await StartCall(client, tool, token, arguments).WaitAsync(TimeSpan.FromSeconds(20), token);

    private static async Task<long> FindManaged(McpClient client, string name, CancellationToken token) =>
        Assert.Single(Read<TreeSearchResult>(await Call(client, "search", token, ("query", name))).Matches,
            node => node.Name == name).Id;

    private static async Task<string?> ReadText(McpClient client, long nodeId, CancellationToken token) =>
        ReadText(await Call(client, "props", token, ("nodeId", nodeId), ("propertyNames", new[] { "Text" })));

    private static string? ReadText(CallToolResult result) => Assert.Single(Read<PropertyInfoDto[]>(result)).Value;

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static T Read<T>(CallToolResult result)
    {
        Assert.False(result.IsError == true, Text(result));
        var root = result.StructuredContent is { } content
            ? JsonNode.Parse(JsonSerializer.Serialize(content))
            : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(WireOptions)!;
    }

    private static void Evidence(string description, object value) =>
        TestContext.Current.TestOutputHelper?.WriteLine(description + ": " + JsonSerializer.Serialize(value));

    private static async Task ObserveCompletion(Task<CallToolResult> request)
    {
        try
        {
            await request.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch (Exception error)
        {
            Evidence("Pending HTTP request ended during owned-target cleanup", new { error.Message });
        }
    }

    private sealed record SampleResult(
        long Sequence,
        string Action,
        bool Cancelled,
        string[] SelectedPaths,
        string? DocumentPath,
        string Content,
        int ByteLength,
        string Sha256,
        string? Error);

    private sealed class SampleProcess : IAsyncDisposable
    {
        private const string Prefix = "avdt-http-native-";
        private readonly Process _process;
        private readonly DateTime _startedUtc;
        private readonly Task<string> _output;
        private readonly Task<string> _errors;
        private readonly string _resultPath;

        private SampleProcess(Process process, string directory, string pipe)
        {
            _process = process;
            _startedUtc = process.StartTime.ToUniversalTime();
            DirectoryPath = directory;
            PipeName = pipe;
            _resultPath = Path.Combine(directory, "result.json");
            _output = process.StandardOutput.ReadToEndAsync();
            _errors = process.StandardError.ReadToEndAsync();
        }

        public string DirectoryPath { get; }
        public string PipeName { get; }
        public int ProcessId => _process.Id;

        public static async Task<SampleProcess> StartAsync(CancellationToken token)
        {
            var directory = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SampleProcess? owned = null;
            try
            {
                var pipe = DiagnosticsProtocol.PipePrefix + "http-native-" + Guid.NewGuid().ToString("N");
                var readyPath = Path.Combine(directory, "ready.txt");
                var samplePath = typeof(HttpNativeMcpTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .Single(item => item.Key == "AutomationSamplePath").Value!;
                var start = new ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                foreach (var argument in new[]
                         {
                             samplePath, "--pipe", pipe, "--test-root", directory,
                             "--ready-file", readyPath, "--result-file", Path.Combine(directory, "result.json")
                         })
                    start.ArgumentList.Add(argument);
                owned = new(Process.Start(start) ??
                            throw new InvalidOperationException("The HTTP desktop sample did not start."),
                    directory,
                    pipe);
                var watch = Stopwatch.StartNew();
                while (!File.Exists(readyPath) && watch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    if (owned._process.HasExited)
                        throw new InvalidOperationException("The HTTP desktop sample exited before readiness: " +
                                                            await owned._errors + await owned._output);
                    await Task.Delay(40, token);
                }

                Assert.True(File.Exists(readyPath), "The owned HTTP desktop sample did not report readiness.");
                var ready = await File.ReadAllTextAsync(readyPath, Encoding.UTF8, token);
                Assert.Contains($"pid={owned.ProcessId}\n", ready);
                Assert.Contains($"pipe={pipe}\n", ready);
                return owned;
            }
            catch
            {
                if (owned is not null)
                    await owned.DisposeAsync();
                else
                    DeleteDirectory(directory);
                throw;
            }
        }

        public async Task<SampleResult> WaitResultAsync(long previousSequence, string action, CancellationToken token)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (File.Exists(_resultPath))
                {
                    var result =
                        JsonSerializer.Deserialize<SampleResult>(
                            await File.ReadAllTextAsync(_resultPath, Encoding.UTF8, token),
                            WireOptions)!;
                    if (result.Sequence > previousSequence)
                    {
                        Assert.Equal(previousSequence + 1, result.Sequence);
                        Assert.Equal(action, result.Action);
                        Assert.False(result.Cancelled);
                        Assert.Null(result.Error);
                        return result;
                    }
                }

                await Task.Delay(40, token);
            }

            throw new TimeoutException($"The owned sample did not finish {action} after sequence {previousSequence}.");
        }

        public async Task StopAsync()
        {
            if (_process.HasExited)
                return;
            if (_process.StartTime.ToUniversalTime() != _startedUtc)
                throw new InvalidOperationException(
                    "The owned HTTP sample process identity changed; refusing to terminate it.");
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync();
                await Task.WhenAll(_output, _errors).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                _process.Dispose();
                DeleteDirectory(DirectoryPath);
            }
        }

        private static void DeleteDirectory(string directory)
        {
            var resolved = Path.GetFullPath(directory);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (!string.Equals(Path.GetDirectoryName(resolved), root, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith(Prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("HTTP native cleanup escaped the owned temporary directory.");
            if (Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }
}
