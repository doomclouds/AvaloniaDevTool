using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AvaloniaDevTool.NativeAutomation;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed class NativeWorkerLifecycleTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory(SkipUnless = nameof(IsWindows), Skip = "Native workers require Windows; these fixtures create no windows.")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_inflight_cancellation_or_timeout_terminates_the_os_worker_and_allows_reattach(bool cancel)
    {
        await using var target = await StartTargetAsync();
        using var directory = new WorkerDirectory();
        NativeAutomationSession? native = null;
        ProcessIdentity? worker = null;
        try
        {
            native = await NativeAutomationSession.AttachAsync(Target(target.Identity), new NativeAutomationOptions
            {
                WorkerPath = directory.ExecutablePath,
                OperationTimeout = TimeSpan.FromSeconds(cancel ? 15 : 3)
            }, TestContext.Current.CancellationToken);
            worker = await ReadStateAsync<ProcessIdentity>(directory.FilePath("attached.json"));
            Assert.Equal(directory.ExecutablePath, worker.ExecutablePath, ignoreCase: true);
            AssertRunning(worker);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var operation = native.GetWindowsAsync(cancellation.Token);
            var blocked = await ReadStateAsync<BlockedState>(directory.FilePath("blocked.json"));
            Assert.Equal("windows", blocked.Command);
            Assert.Equal(worker, blocked.Process);
            Assert.False(operation.IsCompleted);
            var timer = Stopwatch.StartNew();
            if (cancel)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            }
            else
            {
                var error = await Assert.ThrowsAsync<NativeAutomationException>(() => operation);
                Assert.Equal("operation_timeout", error.Code);
            }
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(6), $"The blocked call took {timer.Elapsed} to return.");
            await AssertExitedAsync(worker);
            AssertRunning(target.Identity);
            var expired = await Assert.ThrowsAsync<NativeAutomationException>(() => native.GetWindowsAsync(TestContext.Current.CancellationToken));
            Assert.Equal("session_expired", expired.Code);
            await AssertRealWorkerCanReattachAsync(target.Identity);
        }
        finally
        {
            if (native is not null) await native.DisposeAsync();
            if (worker is not null) await StopOwnedProcessAsync(worker);
        }
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Native workers require Windows; these fixtures create no windows.")]
    public async Task Disposing_a_real_worker_session_preserves_the_target_and_allows_reattach()
    {
        await using var target = await StartTargetAsync();
        await using var native = await NativeAutomationSession.AttachAsync(Target(target.Identity), RealWorkerOptions(), TestContext.Current.CancellationToken);
        Assert.Empty(await native.GetWindowsAsync(TestContext.Current.CancellationToken));
        await native.DisposeAsync();
        AssertRunning(target.Identity);
        var expired = await Assert.ThrowsAsync<NativeAutomationException>(() => native.GetWindowsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("session_expired", expired.Code);
        await AssertRealWorkerCanReattachAsync(target.Identity);
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Native workers require Windows; these fixtures create no windows.")]
    public async Task An_abrupt_parent_exit_reaps_its_real_worker_even_with_stdin_held_open_and_preserves_the_target()
    {
        await using var target = await StartTargetAsync();
        using var current = Process.GetCurrentProcess();
        await using var host = OwnedProcess.Start(FixtureExecutable(), "--native-lifecycle-host", WorkerExecutable(),
            target.Identity.ProcessId.ToString(CultureInfo.InvariantCulture), target.Identity.StartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
            current.Id.ToString(CultureInfo.InvariantCulture), current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        ProcessIdentity? worker = null;
        try
        {
            var state = await host.ReadStateAsync<HostState>();
            using var keepWorkerInputOpen = new SafeFileHandle(new nint(state.InputHandle), ownsHandle: true);
            worker = state.Worker;
            Assert.Equal(host.Identity, state.Host);
            Assert.Equal(WorkerExecutable(), worker.ExecutablePath, ignoreCase: true);
            Assert.False(keepWorkerInputOpen.IsInvalid);
            AssertRunning(worker);
            AssertRunning(target.Identity);
            host.Kill();
            await AssertExitedAsync(host.Identity);
            await AssertExitedAsync(worker);
            AssertRunning(target.Identity);
            await AssertRealWorkerCanReattachAsync(target.Identity);
            GC.KeepAlive(keepWorkerInputOpen);
        }
        finally
        {
            if (worker is not null) await StopOwnedProcessAsync(worker);
        }
    }

    private static async Task AssertRealWorkerCanReattachAsync(ProcessIdentity target)
    {
        await using var fresh = await NativeAutomationSession.AttachAsync(Target(target), RealWorkerOptions(), TestContext.Current.CancellationToken);
        Assert.Empty(await fresh.GetWindowsAsync(TestContext.Current.CancellationToken));
        AssertRunning(target);
    }

    private static NativeTargetIdentity Target(ProcessIdentity identity) => new(identity.ProcessId, new DateTimeOffset(identity.StartTimeUtcTicks, TimeSpan.Zero));
    private static NativeAutomationOptions RealWorkerOptions() => new() { WorkerPath = WorkerExecutable() };
    private static string WorkerExecutable() => Path.ChangeExtension(ArtifactPath("AvaloniaDevTool.NativeAutomation.Worker.dll"), ".exe");
    private static string FixtureExecutable() => Path.ChangeExtension(ArtifactPath("AvaloniaDevTool.NativeAutomation.Fixture.dll"), ".exe");
    private static string ArtifactPath(string filename) => typeof(NativeWorkerLifecycleTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "NativeTestPaths").Value!.Split('|').Single(path => Path.GetFileName(path) == filename);

    private static async Task<OwnedProcess> StartTargetAsync()
    {
        var process = OwnedProcess.Start(FixtureExecutable(), "--native-lifecycle-target");
        try
        {
            Assert.Equal(process.Identity, await process.ReadStateAsync<ProcessIdentity>());
            return process;
        }
        catch { await process.DisposeAsync(); throw; }
    }

    private static async Task<T> ReadStateAsync<T>(string path)
    {
        var timer = Stopwatch.StartNew();
        while (!File.Exists(path) && timer.Elapsed < TimeSpan.FromSeconds(15))
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(path), $"The lifecycle fixture did not publish '{path}'.");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, Encoding.UTF8, TestContext.Current.CancellationToken), JsonOptions)!;
    }

    private static void AssertRunning(ProcessIdentity identity)
    {
        using var process = Process.GetProcessById(identity.ProcessId);
        Assert.False(process.HasExited);
        AssertIdentity(process, identity);
    }

    private static void AssertIdentity(Process process, ProcessIdentity identity)
    {
        Assert.Equal(identity.StartTimeUtcTicks, process.StartTime.ToUniversalTime().Ticks);
        var length = 32768;
        var path = new StringBuilder(length);
        if (!QueryFullProcessImageName(process.SafeHandle, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Assert.Equal(identity.ExecutablePath, Path.GetFullPath(path.ToString()), ignoreCase: true);
    }

    private static bool HasOsProcessExited(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            try
            {
                if (process.HasExited) return true;
                AssertIdentity(process, identity);
                return false;
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException && process.HasExited)
            { return true; }
        }
        catch (ArgumentException) { return true; }
    }

    private static async Task AssertExitedAsync(ProcessIdentity identity)
    {
        var timer = Stopwatch.StartNew();
        while (!HasOsProcessExited(identity) && timer.Elapsed < TimeSpan.FromSeconds(8))
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(HasOsProcessExited(identity), $"Owned process {identity.ProcessId}, started {identity.StartTimeUtcTicks}, '{identity.ExecutablePath}' is still running.");
    }

    private static async Task StopOwnedProcessAsync(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (!process.HasExited)
            {
                AssertIdentity(process, identity);
                process.Kill();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await process.WaitForExitAsync(timeout.Token);
            }
            Assert.True(HasOsProcessExited(identity));
        }
        catch (ArgumentException) { }
    }

    private sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath);
    private sealed record BlockedState(string Command, ProcessIdentity Process);
    private sealed record HostState(ProcessIdentity Host, ProcessIdentity Worker, long InputHandle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref int size);

    private sealed class OwnedProcess(Process process, ProcessIdentity identity) : IAsyncDisposable
    {
        public ProcessIdentity Identity { get; } = identity;
        private readonly Task<string> _diagnostics = process.StandardError.ReadToEndAsync();

        public static OwnedProcess Start(string executablePath, params string[] arguments)
        {
            var start = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{executablePath}'.");
            var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(executablePath));
            AssertIdentity(process, identity);
            return new OwnedProcess(process, identity);
        }

        public async Task<T> ReadStateAsync<T>()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.True(line is not null, _diagnostics.IsCompletedSuccessfully ? _diagnostics.Result : "The lifecycle fixture exited without publishing its identity.");
            return JsonSerializer.Deserialize<T>(line, JsonOptions)!;
        }

        public void Kill()
        {
            AssertRunning(Identity);
            process.Kill();
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopOwnedProcessAsync(Identity); }
            finally { process.Dispose(); }
        }
    }

    private sealed class WorkerDirectory : IDisposable
    {
        private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AvaloniaDevTool.NativeLifecycle", Guid.NewGuid().ToString("N")));
        public string ExecutablePath => FilePath("AvaloniaDevTool.NativeAutomation.Fixture.exe");

        public WorkerDirectory()
        {
            Directory.CreateDirectory(_root);
            var source = Path.GetDirectoryName(FixtureExecutable())!;
            foreach (var filename in new[]
            {
                "AvaloniaDevTool.NativeAutomation.Fixture.exe", "AvaloniaDevTool.NativeAutomation.Fixture.dll",
                "AvaloniaDevTool.NativeAutomation.Fixture.runtimeconfig.json", "AvaloniaDevTool.NativeAutomation.Fixture.deps.json"
            }) File.Copy(Path.Combine(source, filename), FilePath(filename));
            File.WriteAllText(FilePath("native-lifecycle-fixture.json"), JsonSerializer.Serialize(new { controlDirectory = _root }, JsonOptions), new UTF8Encoding(false));
        }

        public string FilePath(string filename) => Path.Combine(_root, filename);

        public void Dispose()
        {
            var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AvaloniaDevTool.NativeLifecycle")) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(_root);
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
                throw new InvalidOperationException("Refusing to remove a lifecycle directory outside this test's temporary root.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
