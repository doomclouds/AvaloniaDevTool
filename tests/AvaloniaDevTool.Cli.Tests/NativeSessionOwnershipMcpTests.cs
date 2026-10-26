using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.Protocol;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

[Collection(NativeDesktopMcpCollection.Name)]
public sealed class NativeSessionOwnershipMcpTests
{
    private static readonly JsonSerializerOptions WireOptions = new(ProtocolJson.Options)
        { Converters = { new JsonStringEnumConverter() } };

    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public static bool IsInteractiveWindows => OperatingSystem.IsWindows() && Environment.UserInteractive;

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task One_stdio_session_switches_native_ownership_and_detach_reaps_the_actual_worker()
    {
        await using var first = await SampleProcess.StartAsync();
        await using var second = await SampleProcess.StartAsync();
        await using var mcp = await StdioMcp.StartAsync();

        await AttachAsync(mcp, first);
        var firstScope = await ReadNativeScopeAsync(mcp, first);
        var firstWorker = mcp.CaptureWorker();
        Assert.Equal("initial text", await ReadInputAsync(mcp));

        await AttachAsync(mcp, second);
        await AssertExitedAsync(firstWorker);
        var secondScope = await ReadNativeScopeAsync(mcp, second);
        var secondWorker = mcp.CaptureWorker();
        Assert.NotEqual(firstScope.Window.NodeId, secondScope.Window.NodeId);
        Assert.NotEqual(firstScope.Editor.NodeId, secondScope.Editor.NodeId);
        Assert.NotEqual(firstWorker.ProcessId, secondWorker.ProcessId);
        Assert.Equal(second.Identity.ProcessId,
            (await mcp.CallAsync("native-inspect", ("nodeId", secondScope.Editor.NodeId)))
            .Read<NativeElementInfo>().ProcessId);

        await AssertStaleAsync(mcp, "native-inspect", ("nodeId", firstScope.Editor.NodeId));
        await AssertStaleAsync(mcp,
            "native-action",
            ("nodeId", firstScope.Editor.NodeId),
            ("action", "SetValue"),
            ("value", "must not cross targets"));
        await AssertStaleAsync(mcp, "native-action", ("nodeId", firstScope.Window.NodeId), ("action", "Close"));
        AssertRunning(first.Identity);
        AssertRunning(second.Identity);
        AssertWindowOwner(firstScope.Window.Handle, first.Identity.ProcessId);
        Assert.Equal("initial text", await ReadInputAsync(mcp));

        // Reattach through the same wire client to verify the first application's state independently.
        await AttachAsync(mcp, first);
        await AssertExitedAsync(secondWorker);
        Assert.Equal("initial text", await ReadInputAsync(mcp));
        var reattached = await ReadNativeScopeAsync(mcp, first);
        var reattachedWorker = mcp.CaptureWorker();
        Assert.Equal(firstScope.Window.Handle, reattached.Window.Handle);
        Assert.NotEqual(firstScope.Editor.NodeId, reattached.Editor.NodeId);
        await AssertStaleAsync(mcp, "native-inspect", ("nodeId", secondScope.Editor.NodeId));

        (await mcp.CallAsync("detach")).AssertSuccess();
        await AssertExitedAsync(reattachedWorker);
        var disconnected = await mcp.CallAsync("native-windows");
        Assert.True(disconnected.IsError);
        Assert.Contains("not_connected", disconnected.Text);
        AssertRunning(first.Identity);
        AssertRunning(second.Identity);
        AssertWindowOwner(firstScope.Window.Handle, first.Identity.ProcessId);
        AssertWindowOwner(secondScope.Window.Handle, second.Identity.ProcessId);
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Closing_the_attached_target_reaps_the_actual_worker_and_the_same_stdio_client_can_attach_again()
    {
        await using var first = await SampleProcess.StartAsync();
        await using var second = await SampleProcess.StartAsync();
        await using var mcp = await StdioMcp.StartAsync();
        await AttachAsync(mcp, first);
        var firstScope = await ReadNativeScopeAsync(mcp, first);
        var firstWorker = mcp.CaptureWorker();

        var close = (await mcp.CallAsync("native-action", ("nodeId", firstScope.Window.NodeId), ("action", "Close")))
            .Read<NativeActionResult>();
        Assert.DoesNotContain(close.Status, new[] { "failed" });
        await AssertExitedAsync(first.Identity);
        await AssertExitedAsync(firstWorker);
        AssertRunning(second.Identity);
        var unavailable = await mcp.CallAsync("native-windows");
        Assert.True(unavailable.IsError);
        Assert.True(new[] { "not_connected", "target_unavailable", "target_exited", "target_changed" }
                .Any(code => unavailable.Text.Contains(code, StringComparison.Ordinal)),
            unavailable.Text);

        await AttachAsync(mcp, second);
        var secondScope = await ReadNativeScopeAsync(mcp, second);
        var secondWorker = mcp.CaptureWorker();
        Assert.NotEqual(firstScope.Window.NodeId, secondScope.Window.NodeId);
        await AssertStaleAsync(mcp, "native-action", ("nodeId", firstScope.Window.NodeId), ("action", "Close"));
        Assert.Equal("initial text", await ReadInputAsync(mcp));
        AssertWindowOwner(secondScope.Window.Handle, second.Identity.ProcessId);
        (await mcp.CallAsync("detach")).AssertSuccess();
        await AssertExitedAsync(secondWorker);
        AssertRunning(second.Identity);
    }

    private static async Task AttachAsync(StdioMcp mcp, SampleProcess sample)
    {
        var applications = (await mcp.CallAsync("attach-to-app")).Read<OfficialAttachmentResult>();
        var application = Assert.Single(applications.Applications, candidate => candidate.PipeName == sample.PipeName);
        var attached = (await mcp.CallAsync("attach-to-app", ("id", application.Id))).Read<OfficialAttachmentResult>();
        Assert.True(attached.Attached);
        Assert.Equal(sample.Identity.ProcessId, attached.Session!.ProcessId);
    }

    private static async Task<NativeScope> ReadNativeScopeAsync(StdioMcp mcp, SampleProcess sample)
    {
        var windows = (await mcp.CallAsync("native-windows")).Read<NativeWindowInfo[]>();
        Assert.All(windows, window => Assert.Equal(sample.Identity.ProcessId, window.ProcessId));
        var main = Assert.Single(windows, window => window.Title == "AvaloniaDevTool automation acceptance");
        AssertWindowOwner(main.Handle, sample.Identity.ProcessId);
        var tree = (await mcp.CallAsync("native-tree",
                ("rootNodeId", main.NodeId),
                ("maxDepth", 16),
                ("maxNodes", 2000)))
            .Read<NativeTreeResult>();
        Assert.False(tree.Truncated);
        Assert.All(tree.Nodes, node => Assert.Equal(sample.Identity.ProcessId, node.ProcessId));
        var editor = Assert.Single(tree.Nodes, node => node.AutomationId == "InputTextBox");
        Assert.StartsWith("native:", editor.NodeId);
        return new NativeScope(main, editor);
    }

    private static async Task<string?> ReadInputAsync(StdioMcp mcp)
    {
        var node = Assert.Single(
            (await mcp.CallAsync("search", ("query", "InputTextBox"))).Read<TreeSearchResult>().Matches,
            candidate => candidate.Name == "InputTextBox");
        return Assert.Single((await mcp.CallAsync("props",
            ("nodeId", node.Id),
            ("propertyNames", new[] { "Avalonia.Controls.TextBlock:Text" }))).Read<PropertyInfoDto[]>()).Value;
    }

    private static async Task AssertStaleAsync(StdioMcp mcp,
        string tool,
        params (string Name, object? Value)[] arguments)
    {
        var failure = await mcp.CallAsync(tool, arguments);
        Assert.True(failure.IsError);
        Assert.Contains("stale_node", failure.Text);
    }

    private static string Metadata(string key) => typeof(NativeSessionOwnershipMcpTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == key).Value!;

    private static ProcessIdentity CaptureIdentity(Process process)
    {
        var length = 32768;
        var path = new StringBuilder(length);
        if (!QueryFullProcessImageName(process.SafeHandle, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return new ProcessIdentity(process.Id,
            process.StartTime.ToUniversalTime().Ticks,
            Path.GetFullPath(path.ToString()));
    }

    private static void AssertRunning(ProcessIdentity identity)
    {
        using var process = Process.GetProcessById(identity.ProcessId);
        Assert.False(process.HasExited);
        Assert.Equal(identity, CaptureIdentity(process));
    }

    private static bool HasExited(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            try
            {
                if (process.HasExited)
                    return true;
                Assert.Equal(identity, CaptureIdentity(process));
                return false;
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException && process.HasExited)
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task AssertExitedAsync(ProcessIdentity identity)
    {
        var watch = Stopwatch.StartNew();
        while (!HasExited(identity) && watch.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(25, Token);
        Assert.True(HasExited(identity),
            $"Owned process {identity.ProcessId}, started {identity.StartTimeUtcTicks}, '{identity.ExecutablePath}' is still running.");
    }

    private static async Task StopOwnedAsync(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (!process.HasExited)
            {
                Assert.Equal(identity, CaptureIdentity(process));
                process.Kill();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
            }

            Assert.True(HasExited(identity));
        }
        catch (ArgumentException)
        {
        }
    }

    private static void AssertWindowOwner(long handle, int processId)
    {
        Assert.True(IsWindow(new nint(handle)));
        Assert.NotEqual(0u, GetWindowThreadProcessId(new nint(handle), out var owner));
        Assert.Equal((uint)processId, owner);
    }

    private sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath);

    private sealed record NativeScope(NativeWindowInfo Window, NativeElementInfo Editor);

    private sealed class ToolResponse(JsonElement result)
    {
        public bool IsError => result.TryGetProperty("isError", out var error) && error.GetBoolean();

        public string Text => string.Join("\n",
            result.GetProperty("content").EnumerateArray()
                .Where(block => block.GetProperty("type").GetString() == "text")
                .Select(block => block.GetProperty("text").GetString()));

        public void AssertSuccess() => Assert.False(IsError, Text);

        public T Read<T>()
        {
            AssertSuccess();
            var value = result.TryGetProperty("structuredContent", out var content)
                ? content
                : JsonSerializer.Deserialize<JsonElement>(Text);
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("result", out var wrapped))
                value = wrapped;
            return value.Deserialize<T>(WireOptions)!;
        }
    }

    private sealed class StdioMcp(OwnedProcess owned) : IAsyncDisposable
    {
        private readonly List<ProcessIdentity> _workers = [];
        private long _request;

        public static async Task<StdioMcp> StartAsync()
        {
            var owner = new StdioMcp(OwnedProcess.Start(Path.ChangeExtension(Metadata("PreviewCliPath"), ".exe"),
                false,
                "mcp"));
            try
            {
                await owner.RequestAsync("initialize",
                    new
                    {
                        protocolVersion = "2025-06-18", capabilities = new { },
                        clientInfo = new { name = "Native session ownership MCP tests", version = "1" }
                    });
                await owner.WriteAsync(new { jsonrpc = "2.0", method = "notifications/initialized" });
                return owner;
            }
            catch
            {
                await owner.DisposeAsync();
                throw;
            }
        }

        public async Task<ToolResponse> CallAsync(string name, params (string Name, object? Value)[] arguments) =>
            new(await RequestAsync("tools/call",
                new { name, arguments = arguments.ToDictionary(item => item.Name, item => item.Value) }));

        private async Task<JsonElement> RequestAsync(string method, object parameters)
        {
            var id = ++_request;
            await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (await owned.Process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
            {
                using var response = JsonDocument.Parse(line);
                var root = response.RootElement;
                if (!root.TryGetProperty("id", out var returned) || !returned.TryGetInt64(out var returnedId) ||
                    returnedId != id)
                    continue;
                Assert.False(root.TryGetProperty("error", out _), root.GetRawText());
                return root.GetProperty("result").Clone();
            }

            throw new InvalidOperationException("The owned stdio MCP process exited before responding.");
        }

        private async Task WriteAsync(object message)
        {
            await owned.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), Token);
            await owned.Process.StandardInput.FlushAsync(Token);
        }

        public ProcessIdentity CaptureWorker()
        {
            AssertRunning(owned.Identity);
            var children = SnapshotChildren(owned.Identity.ProcessId);
            var child = Assert.Single(children,
                entry => string.Equals(entry.ExecutableFile, "dotnet.exe", StringComparison.OrdinalIgnoreCase));
            using var process = Process.GetProcessById((int)child.ProcessId);
            var identity = CaptureIdentity(process);
            Assert.True(identity.StartTimeUtcTicks >= owned.Identity.StartTimeUtcTicks);
            Assert.Equal("dotnet.exe", Path.GetFileName(identity.ExecutablePath), ignoreCase: true);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Metadata("PreviewCliPath"))!,
                "native-worker",
                "AvaloniaDevTool.NativeAutomation.Worker.dll")));
            if (!_workers.Contains(identity))
                _workers.Add(identity);
            return identity;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!HasExited(owned.Identity))
                {
                    if (SnapshotChildren(owned.Identity.ProcessId).Any(entry =>
                            string.Equals(entry.ExecutableFile, "dotnet.exe", StringComparison.OrdinalIgnoreCase)))
                        CaptureWorker();
                    owned.Process.StandardInput.Close();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try
                    {
                        await owned.Process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        await StopOwnedAsync(owned.Identity);
                    }
                }
            }
            finally
            {
                foreach (var worker in _workers)
                    await StopOwnedAsync(worker);
                await owned.DisposeAsync();
            }
        }
    }

    private sealed class OwnedProcess(Process process, bool drainOutput) : IAsyncDisposable
    {
        public Process Process { get; } = process;
        public ProcessIdentity Identity { get; } = CaptureIdentity(process);
        private readonly Task _output = drainOutput ? process.StandardOutput.ReadToEndAsync() : Task.CompletedTask;
        private readonly Task _errors = process.StandardError.ReadToEndAsync();

        public static OwnedProcess Start(string executable, bool drainOutput, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            return new OwnedProcess(Process.Start(start) ??
                                    throw new InvalidOperationException($"Could not start '{executable}'."),
                drainOutput);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopOwnedAsync(Identity);
                await Task.WhenAll(_output, _errors);
            }
            finally
            {
                Process.Dispose();
            }
        }
    }

    private sealed class SampleProcess(OwnedProcess owned, string directory, string pipeName) : IAsyncDisposable
    {
        public ProcessIdentity Identity => owned.Identity;
        public string PipeName => pipeName;

        public static async Task<SampleProcess> StartAsync()
        {
            var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "avdt-native-session-mcp-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            var pipe = DiagnosticsProtocol.PipePrefix + "native-session-" + Guid.NewGuid().ToString("N");
            var ready = Path.Combine(directory, "ready.txt");
            var sample = new SampleProcess(OwnedProcess.Start(
                    Path.ChangeExtension(Metadata("AutomationSamplePath"), ".exe"),
                    true,
                    "--pipe",
                    pipe,
                    "--test-root",
                    directory,
                    "--ready-file",
                    ready),
                directory,
                pipe);
            try
            {
                var watch = Stopwatch.StartNew();
                while (!File.Exists(ready) && watch.Elapsed < TimeSpan.FromSeconds(20))
                {
                    AssertRunning(sample.Identity);
                    await Task.Delay(50, Token);
                }

                Assert.True(File.Exists(ready), "The owned automation sample did not report readiness.");
                var text = await File.ReadAllTextAsync(ready, Encoding.UTF8, Token);
                Assert.Contains($"pid={sample.Identity.ProcessId}\n", text);
                Assert.Contains($"pipe={pipe}\n", text);
                return sample;
            }
            catch
            {
                await sample.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await owned.DisposeAsync();
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) +
                       Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(temp + "avdt-native-session-mcp-", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(resolved) + Path.DirectorySeparatorChar,
                    temp,
                    StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(resolved)["avdt-native-session-mcp-".Length..], "N", out _))
                throw new InvalidOperationException("Refusing to clean up an unowned temporary directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static List<ProcessEntry> SnapshotChildren(int parentId)
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var children = new List<ProcessEntry>();
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), ExecutableFile = "" };
        if (!Process32First(snapshot, ref entry))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        do
        {
            if (entry.ParentProcessId == (uint)parentId)
                children.Add(entry);
        } while (Process32Next(snapshot, ref entry));

        return children;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process,
        uint flags,
        StringBuilder path,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}
