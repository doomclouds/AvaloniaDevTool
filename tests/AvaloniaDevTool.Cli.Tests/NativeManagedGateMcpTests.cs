using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

[Collection(NativeDesktopMcpCollection.Name)]
public sealed class NativeManagedGateMcpTests
{
    private const string DialogTitle = "Automation sample - Managed gate";
    private const string TextProperty = "Avalonia.Controls.TextBlock:Text";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public static bool IsInteractiveWindows => OperatingSystem.IsWindows() && Environment.UserInteractive;

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_tools_inspect_and_answer_an_owned_dialog_while_the_original_click_and_another_managed_call_remain_pending()
    {
        await using var sample = await SampleProcess.StartAsync();
        using var calls = CancellationTokenSource.CreateLinkedTokenSource(Token);
        calls.CancelAfter(TimeSpan.FromSeconds(60));
        McpClient? client = null;
        Task<CallToolResult>? originalClick = null;
        Task<CallToolResult>? queuedManagedRead = null;
        try
        {
            client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = "dotnet", Name = "Native managed gate acceptance stdio",
                Arguments = [Metadata("PreviewCliPath"), "mcp", "--pipe", sample.PipeName],
                ShutdownTimeout = TimeSpan.FromSeconds(5)
            }), cancellationToken: calls.Token);
            var button = await FindManaged(client, "BlockManagedGateButton", calls.Token);
            var status = await FindManaged(client, "ManagedGateStatus", calls.Token);
            Assert.Equal("Ready", ReadText(await Call(client, "props", calls.Token,
                ("nodeId", status), ("propertyNames", new[] { TextProperty }))));
            Assert.True(Read<ElementActionResult>(await Call(client, "action", calls.Token,
                ("nodeId", button), ("action", "BringIntoView"))).Succeeded);
            var main = Assert.Single(Read<NativeWindowInfo[]>(await Call(client, "native-windows", calls.Token)),
                window => window.ProcessId == sample.ProcessId && window.Title == "AvaloniaDevTool automation acceptance");
            Assert.NotEqual(0, main.Handle);
            Assert.False(main.IsOwned);

            // Keep the actual SDK call task, rather than substituting a delayed task for a busy managed gate.
            originalClick = StartCall(client, "ui-click", calls.Token, ("nodeId", button));
            var dialog = await WaitDialog(client, sample.ProcessId, main.Handle, calls.Token);
            Evidence("Owned native modal while original click is active", new
            { sample.ProcessId, main, dialog, OriginalClickStatus = originalClick.Status.ToString(), originalClick.IsCompleted });
            Assert.False(originalClick.IsCompleted, "The synchronous MessageBoxW must still occupy the original managed click.");
            queuedManagedRead = StartCall(client, "props", calls.Token,
                ("nodeId", status), ("propertyNames", new[] { TextProperty }));
            await Task.Delay(150, calls.Token);
            AssertPending(originalClick, queuedManagedRead);

            var during = Read<NativeWindowInfo[]>(await Call(client, "native-windows", calls.Token));
            Evidence("Native windows responded while both managed tasks remain active", new
            { OriginalClick = originalClick.Status.ToString(), QueuedManagedRead = queuedManagedRead.Status.ToString(), Windows = during });
            Assert.All(during, window => Assert.Equal(sample.ProcessId, window.ProcessId));
            Assert.Contains(during, window => window.Handle == dialog.Handle && window.OwnerHandle == main.Handle && window.ClassName == "#32770");
            Assert.False(Assert.Single(during, window => window.Handle == main.Handle).IsEnabled);
            var buttons = Read<NativeFindResult>(await Call(client, "native-find", calls.Token,
                ("rootNodeId", dialog.NodeId), ("automationId", "1"), ("controlType", "Button"), ("maxDepth", 4), ("maxNodes", 100)));
            Assert.False(buttons.Truncated);
            if (buttons.Matches.Length == 0)
            {
                Evidence("Standard OK filter did not resolve exactly one button", buttons);
                var actualTree = Read<NativeTreeResult>(await Call(client, "native-tree", calls.Token,
                    ("rootNodeId", dialog.NodeId), ("maxDepth", 6), ("maxNodes", 200)));
                Evidence("Actual owned dialog tree", actualTree);
                Assert.False(actualTree.Truncated);
                var singleButton = Assert.Single(actualTree.Nodes,
                    node => node.ControlType == "Button" && node.ClassName == "Button" && node.NativeWindowHandle != 0);
                // Some Windows MessageBox implementations expose the sole OK control as IDCANCEL (2) so its caption close is equivalent.
                Assert.Equal("2", singleButton.AutomationId);
                Assert.Contains(singleButton.Name, new[] { "OK", "确定" });
                Assert.Equal(2, GetDlgCtrlID(new nint(singleButton.NativeWindowHandle)));
                buttons = Read<NativeFindResult>(await Call(client, "native-find", calls.Token,
                    ("rootNodeId", dialog.NodeId), ("automationId", "2"), ("className", "Button"),
                    ("controlType", "Button"), ("maxDepth", 4), ("maxNodes", 100)));
                Assert.False(buttons.Truncated);
                Evidence("Verified sole standard OK button alias", new { Button = singleButton, Win32ControlId = 2 });
            }
            var ok = Assert.Single(buttons.Matches);
            Assert.Equal(sample.ProcessId, ok.ProcessId);
            Assert.Contains(ok.AutomationId, new[] { "1", "2" });
            Assert.True(ok.IsEnabled);
            Assert.Contains("Invoke", ok.SupportedPatterns);
            AssertPending(originalClick, queuedManagedRead);

            var answered = Read<NativeActionResult>(await Call(client, "native-action", calls.Token,
                ("nodeId", ok.NodeId), ("action", "Invoke")));
            Assert.Equal("dispatched", answered.Status);
            Assert.True(Read<InputResult>(await originalClick.WaitAsync(TimeSpan.FromSeconds(10), calls.Token)).Delivered);
            Assert.Equal("Returned", ReadText(await queuedManagedRead.WaitAsync(TimeSpan.FromSeconds(10), calls.Token)));
            Assert.Equal("Returned", ReadText(await Call(client, "props", calls.Token,
                ("nodeId", status), ("propertyNames", new[] { TextProperty }))));
            var after = Read<NativeWindowInfo[]>(await Call(client, "native-windows", calls.Token));
            Assert.DoesNotContain(after, window => window.Handle == dialog.Handle || window.Title == DialogTitle);
            Assert.True(Assert.Single(after, window => window.Handle == main.Handle).IsEnabled);
        }
        finally
        {
            // End only our owned target before disposing the SDK-owned CLI/worker transport, even after an assertion fails in the modal loop.
            calls.Cancel();
            await sample.StopAsync();
            if (client is not null) await client.DisposeAsync();
            await ObserveCompletion(originalClick);
            await ObserveCompletion(queuedManagedRead);
        }
    }

    private static void AssertPending(Task<CallToolResult> originalClick, Task<CallToolResult> queuedManagedRead)
    {
        Evidence("Actual managed task completion states", new
        { OriginalClickCompleted = originalClick.IsCompleted, QueuedManagedReadCompleted = queuedManagedRead.IsCompleted });
        Assert.False(originalClick.IsCompleted, "The original managed click returned before native OK was invoked.");
        Assert.False(queuedManagedRead.IsCompleted, "A second managed read passed the gate while the original click was still active.");
    }

    private static async Task<NativeWindowInfo> WaitDialog(McpClient client, int processId, long owner, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            var windows = Read<NativeWindowInfo[]>(await Call(client, "native-windows", token));
            Assert.All(windows, window => Assert.Equal(processId, window.ProcessId));
            if (windows.SingleOrDefault(window => window.Title == DialogTitle) is { } dialog)
            {
                Assert.Equal("#32770", dialog.ClassName);
                Assert.Equal(owner, dialog.OwnerHandle);
                Assert.True(dialog.IsOwned);
                Assert.True(dialog.IsVisible);
                return dialog;
            }
            await Task.Delay(50, token);
        }
        throw new TimeoutException("The real owned MessageBoxW did not become visible through native-windows.");
    }

    private static Task<CallToolResult> StartCall(McpClient client, string name, CancellationToken token,
        params (string Name, object? Value)[] values) => client.CallToolAsync(name,
        values.ToDictionary(item => item.Name, item => item.Value), cancellationToken: token).AsTask();

    private static async Task<CallToolResult> Call(McpClient client, string name, CancellationToken token,
        params (string Name, object? Value)[] values) =>
        await StartCall(client, name, token, values).WaitAsync(TimeSpan.FromSeconds(20), token);

    private static async Task<long> FindManaged(McpClient client, string name, CancellationToken token) => Assert.Single(
        Read<TreeSearchResult>(await Call(client, "search", token, ("query", name))).Matches, node => node.Name == name).Id;

    private static string? ReadText(CallToolResult result) => Assert.Single(Read<PropertyInfoDto[]>(result), property => property.Id == TextProperty).Value;

    private static string Metadata(string key) => typeof(NativeManagedGateMcpTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == key).Value!;

    private static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static void Evidence(string description, object actual) =>
        TestContext.Current.TestOutputHelper?.WriteLine(description + ": " + JsonSerializer.Serialize(actual, ProtocolJson.Options));

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetDlgCtrlID(nint window);

    private static T Read<T>(CallToolResult result)
    {
        Assert.False(result.IsError == true, Text(result));
        var root = result.StructuredContent is { } content ? JsonNode.Parse(JsonSerializer.Serialize(content)) : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped)) root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }

    private static async Task ObserveCompletion(Task<CallToolResult>? pending)
    {
        if (pending is null) return;
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException or ModelContextProtocol.McpException) { }
    }

    private sealed class SampleProcess : IAsyncDisposable
    {
        private const string Prefix = "avdt-managed-gate-mcp-";
        private readonly Process _process;
        private readonly DateTime _startedUtc;
        private readonly Task<string> _output;
        private readonly Task<string> _errors;
        private SampleProcess(Process process, string directory, string pipe)
        {
            _process = process; _startedUtc = process.StartTime.ToUniversalTime();
            DirectoryPath = directory; PipeName = pipe;
            _output = process.StandardOutput.ReadToEndAsync(); _errors = process.StandardError.ReadToEndAsync();
        }
        public string DirectoryPath { get; }
        public string PipeName { get; }
        public int ProcessId => _process.Id;

        public static async Task<SampleProcess> StartAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var pipe = DiagnosticsProtocol.PipePrefix + "managed-gate-" + Guid.NewGuid().ToString("N");
            var ready = Path.Combine(directory, "ready.txt");
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in new[] { Metadata("AutomationSamplePath"), "--pipe", pipe,
                "--test-root", directory, "--ready-file", ready }) start.ArgumentList.Add(argument);
            SampleProcess? owned = null;
            try
            {
                owned = new SampleProcess(Process.Start(start) ?? throw new InvalidOperationException("The owned sample did not start."), directory, pipe);
                var watch = Stopwatch.StartNew();
                while (!File.Exists(ready) && watch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    Assert.False(owned._process.HasExited, "The owned sample exited before readiness.");
                    await Task.Delay(50, Token);
                }
                Assert.True(File.Exists(ready), "The owned sample did not report readiness.");
                var text = await File.ReadAllTextAsync(ready, Encoding.UTF8, Token);
                Assert.Contains($"pid={owned.ProcessId}\n", text);
                Assert.Contains($"pipe={pipe}\n", text);
                return owned;
            }
            catch
            {
                if (owned is not null) await owned.DisposeAsync();
                else DeleteDirectory(directory);
                throw;
            }
        }

        public async Task StopAsync()
        {
            if (_process.HasExited) return;
            if (_process.StartTime.ToUniversalTime() != _startedUtc)
                throw new InvalidOperationException("The owned sample's process identity changed; refusing to terminate it.");
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await Task.WhenAll(_output, _errors);
            _process.Dispose();
            DeleteDirectory(DirectoryPath);
        }

        private static void DeleteDirectory(string directory)
        {
            var resolved = Path.GetFullPath(directory);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(temp + Prefix, StringComparison.OrdinalIgnoreCase) ||
                Path.GetDirectoryName(resolved) + Path.DirectorySeparatorChar != temp)
                throw new InvalidOperationException("Temporary cleanup escaped the managed gate fixture's owned directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
