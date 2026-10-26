using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AvaloniaDevTool.NativeAutomation;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed class NativeFileFilterTimingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(SkipUnless = nameof(IsWindows),
        Skip = "The synthetic provider checks a transient truncated tree without creating windows.")]
    public async Task A_transient_incomplete_dialog_read_retries_the_same_identity_before_cancelling_exactly_once()
    {
        await using var worker = await TimingWorker.StartAsync(exposeOptions: false, truncatedDialogRead: 2);
        var dialogs = new NativeFileDialogController(worker.Session);
        var original = Assert.Single(await dialogs.GetDialogsAsync(Token));
        var discovered = await worker.ReadTraceAsync();
        Assert.Equal(1, discovered.DialogReads);
        Assert.Equal(0, discovered.TruncatedDialogReads);
        Assert.Empty(discovered.Actions);

        var cancelled = await dialogs.CancelAsync(original.DialogId, Token);

        Assert.Equal(original.DialogId, cancelled.DialogId);
        Assert.Equal("CancelledDialogClosed", cancelled.State);
        Assert.False(cancelled.ApplicationIoVerified);
        worker.AssertOsProcessRunning();
        var trace = await worker.ReadTraceAsync();
        Assert.Equal(3, trace.DialogReads);
        Assert.Equal(1, trace.TruncatedDialogReads);
        Assert.False(trace.Open);
        var action = Assert.Single(trace.Actions);
        Assert.Equal(original.CancelNodeId, action.NodeId);
        Assert.Equal("Invoke", action.Action);
    }

    [Fact(SkipUnless = nameof(IsWindows),
        Skip = "The synthetic provider exercises an acknowledged but unapplied Expand without creating windows.")]
    public async Task
        A_completed_expand_that_remains_collapsed_uses_its_unique_dropdown_button_once_and_verifies_selection()
    {
        await using var worker = await TimingWorker.StartAsync(exposeOptions: true, ignoreExpand: true);
        var dialogs = new NativeFileDialogController(worker.Session);
        var original = Assert.Single(await dialogs.GetDialogsAsync(Token));

        var changed = await dialogs.SetFilterAsync(original.DialogId, filterIndex: 1, cancellationToken: Token);

        Assert.Equal("Text files (*.txt)", changed.FilterValue);
        worker.AssertOsProcessRunning();
        var trace = await worker.ReadTraceAsync();
        Assert.Single(trace.Actions, item => item.Action == "Expand");
        Assert.Single(trace.Actions,
            item => item.Action == "Invoke" && item.NodeId.EndsWith(":dropdown", StringComparison.Ordinal));
        Assert.Single(trace.Actions, item => item.Action == "Select");
        Assert.Equal("CancelledDialogClosed", (await dialogs.CancelAsync(original.DialogId, Token)).State);
    }

    [Theory(SkipUnless = nameof(IsWindows),
        Skip = "The synthetic provider exercises process/RPC timing without creating windows.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_dialog_polling_deadline_does_not_cancel_a_slow_native_request_or_destroy_the_session(
        bool waitForClosure)
    {
        await using var worker = await TimingWorker.StartAsync(exposeOptions: false,
            slowWindowsRead: waitForClosure ? 2 : 1,
            omitSlowWindowsResult: !waitForClosure);
        var dialogs = new NativeFileDialogController(worker.Session);
        var original = waitForClosure ? Assert.Single(await dialogs.GetDialogsAsync(Token)) : null;
        var timer = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<NativeAutomationException>(() => waitForClosure
            ? dialogs.WaitForClosedAsync(original!.DialogId, timeoutMs: 100, cancellationToken: Token)
            : dialogs.WaitForDialogAsync(timeoutMs: 100, cancellationToken: Token));

        Assert.Equal("file_dialog_timeout", error.Code);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(500),
            "A polling deadline must let the in-flight bounded RPC finish.");
        worker.AssertOsProcessRunning();
        var current = Assert.Single(await dialogs.GetDialogsAsync(Token));
        if (original is not null)
            Assert.Equal(original.DialogId, current.DialogId);
        Assert.Equal("Open", (await dialogs.InspectAsync(current.DialogId, Token)).State);
        Assert.Equal("CancelledDialogClosed", (await dialogs.CancelAsync(current.DialogId, Token)).State);
        worker.AssertOsProcessRunning();
        var trace = await worker.ReadTraceAsync();
        Assert.Single(trace.Actions);
        Assert.Equal(current.CancelNodeId, trace.Actions[0].NodeId);
    }

    [Fact(SkipUnless = nameof(IsWindows),
        Skip = "The synthetic provider exercises exact path readback without creating windows.")]
    public async Task A_path_write_readback_mismatch_never_accepts_and_a_verified_retry_can_recover()
    {
        await using var worker = await TimingWorker.StartAsync(exposeOptions: false, corruptFirstPathWrite: true);
        var dialogs = new NativeFileDialogController(worker.Session);
        var original = Assert.Single(await dialogs.GetDialogsAsync(Token));
        var path = Path.Combine(Path.GetTempPath(), "avdt-readback-" + Guid.NewGuid().ToString("N") + ".txt");

        var error = await Assert.ThrowsAsync<NativeAutomationException>(() => dialogs.SetPathsAsync(original.DialogId,
            [path],
            NativeFileDialogKind.SaveFile,
            Token));

        Assert.Equal("file_dialog_readback_mismatch", error.Code);
        worker.AssertOsProcessRunning();
        var changed = await dialogs.InspectAsync(original.DialogId, Token);
        Assert.Equal("Open", changed.State);
        Assert.Equal(path + ".unexpected", changed.PathInputValue);
        Assert.DoesNotContain((await worker.ReadTraceAsync()).Actions, item => item.Action == "Invoke");

        var recovered = await dialogs.SetPathsAsync(original.DialogId, [path], NativeFileDialogKind.SaveFile, Token);
        Assert.Equal(path, recovered.PathInputValue);
        Assert.Equal(new[] { path }, recovered.RequestedPaths);
        Assert.False(recovered.ApplicationIoVerified);
        Assert.Equal("CancelledDialogClosed", (await dialogs.CancelAsync(original.DialogId, Token)).State);
        var actions = (await worker.ReadTraceAsync()).Actions;
        Assert.Equal(2, actions.Count(item => item.Action == "SetValue"));
        Assert.Single(actions, item => item.Action == "Invoke" && item.NodeId == original.CancelNodeId);
        Assert.DoesNotContain(actions, item => item.NodeId == original.AcceptNodeId);
        Assert.False(File.Exists(path));
    }

    [Fact(SkipUnless = nameof(IsWindows),
        Skip = "The process facade requires Windows; this synthetic provider creates no windows.")]
    public async Task
        A_filter_tree_response_slower_than_the_visibility_budget_preserves_the_worker_and_dialog_identity()
    {
        await using var worker = await TimingWorker.StartAsync(exposeOptions: true);
        var dialogs = new NativeFileDialogController(worker.Session);
        var original = Assert.Single(await dialogs.GetDialogsAsync(Token));
        var timer = Stopwatch.StartNew();

        var changed = await dialogs.SetFilterAsync(original.DialogId, filterIndex: 1, cancellationToken: Token);

        Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(3.3),
            $"The synthetic provider response returned too soon: {timer.Elapsed}.");
        Assert.Equal(original.DialogId, changed.DialogId);
        Assert.Equal("Text files (*.txt)", changed.FilterValue);
        worker.AssertOsProcessRunning();
        var inspected = await dialogs.InspectAsync(original.DialogId, Token);
        Assert.Equal(original.DialogId, inspected.DialogId);
        Assert.Equal("Open", inspected.State);
        Assert.Equal("Text files (*.txt)", inspected.FilterValue);
        var trace = await worker.ReadTraceAsync();
        Assert.Equal(1, trace.ComboReads);
        Assert.Single(trace.Actions, item => item.Action == "Expand");
        Assert.Single(trace.Actions, item => item.Action == "Select");
        Assert.Equal("CancelledDialogClosed", (await dialogs.CancelAsync(original.DialogId, Token)).State);
        worker.AssertOsProcessRunning();
    }

    [Fact(SkipUnless = nameof(IsWindows),
        Skip = "The process facade requires Windows; this synthetic provider creates no windows.")]
    public async Task
        A_filter_visibility_timeout_performs_no_selection_and_keeps_the_same_dialog_inspectable_and_cancellable()
    {
        await using var worker = await TimingWorker.StartAsync(exposeOptions: false);
        var dialogs = new NativeFileDialogController(worker.Session);
        var original = Assert.Single(await dialogs.GetDialogsAsync(Token));
        var timer = Stopwatch.StartNew();

        var timeout = await Assert.ThrowsAsync<NativeAutomationException>(() => dialogs.SetFilterAsync(
            original.DialogId,
            filterIndex: 1,
            cancellationToken: Token));

        Assert.Equal("file_filter_timeout", timeout.Code);
        await Task.Delay(100, Token);
        worker.AssertOsProcessRunning();
        Assert.True(timer.Elapsed >= TimeSpan.FromSeconds(3.3));
        var inspected = await dialogs.InspectAsync(original.DialogId, Token);
        Assert.Equal(original.DialogId, inspected.DialogId);
        Assert.Equal("Open", inspected.State);
        Assert.Equal(original.FilterValue, inspected.FilterValue);
        var beforeCancel = await worker.ReadTraceAsync();
        Assert.Equal(1, beforeCancel.ComboReads);
        Assert.Single(beforeCancel.Actions, item => item.Action == "Expand");
        Assert.DoesNotContain(beforeCancel.Actions, item => item.Action == "Select");
        Assert.Equal("CancelledDialogClosed", (await dialogs.CancelAsync(original.DialogId, Token)).State);
        worker.AssertOsProcessRunning();
        var afterCancel = await worker.ReadTraceAsync();
        Assert.DoesNotContain(afterCancel.Actions, item => item.Action == "Select");
        Assert.Single(afterCancel.Actions, item => item.Action == "Invoke" && item.NodeId == original.CancelNodeId);
    }

    [Theory(SkipUnless = nameof(IsWindows),
        Skip = "The synthetic provider checks fallback guards without creating windows.")]
    [InlineData(false, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 2)]
    public async Task An_expanded_pending_or_ambiguous_filter_never_invokes_a_dropdown_and_remains_cancellable(
        bool ignoreExpand,
        bool pendingExpand,
        int dropDownButtonCount)
    {
        await using var worker = await TimingWorker.StartAsync(exposeOptions: false,
            ignoreExpand: ignoreExpand,
            pendingExpand: pendingExpand,
            dropDownButtonCount: dropDownButtonCount);
        var dialogs = new NativeFileDialogController(worker.Session);
        var original = Assert.Single(await dialogs.GetDialogsAsync(Token));

        var error = await Assert.ThrowsAsync<NativeAutomationException>(() => dialogs.SetFilterAsync(
            original.DialogId,
            filterIndex: 1,
            cancellationToken: Token));

        Assert.Equal("file_filter_timeout", error.Code);
        worker.AssertOsProcessRunning();
        var inspected = await dialogs.InspectAsync(original.DialogId, Token);
        Assert.Equal("Open", inspected.State);
        Assert.Equal(original.DialogId, inspected.DialogId);
        Assert.Equal(original.FilterValue, inspected.FilterValue);
        var trace = await worker.ReadTraceAsync();
        Assert.Single(trace.Actions, item => item.Action == "Expand");
        Assert.DoesNotContain(trace.Actions, item => item.Action is "Invoke" or "Select");
        if (pendingExpand)
            Assert.True(trace.ActionStatusReads > 0);
        Assert.Equal("CancelledDialogClosed", (await dialogs.CancelAsync(original.DialogId, Token)).State);
        worker.AssertOsProcessRunning();
        var afterCancel = await worker.ReadTraceAsync();
        Assert.Single(afterCancel.Actions, item => item.Action == "Invoke" && item.NodeId == original.CancelNodeId);
        Assert.DoesNotContain(afterCancel.Actions,
            item => item.Action == "Select" ||
                    item.Action == "Invoke" && item.NodeId != original.CancelNodeId);
    }

    private sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath);

    private sealed record ActionEntry(string NodeId, string Action);

    private sealed record WorkerTrace(
        ActionEntry[] Actions,
        int ComboReads,
        bool Open,
        string FilterValue,
        int ActionStatusReads,
        int DialogReads,
        int TruncatedDialogReads);

    private sealed class TimingWorker(NativeAutomationSession session, ProcessIdentity identity, string directory)
        : IAsyncDisposable
    {
        public NativeAutomationSession Session { get; } = session;

        public static async Task<TimingWorker> StartAsync(bool exposeOptions,
            int slowWindowsRead = 0,
            bool omitSlowWindowsResult = false,
            bool corruptFirstPathWrite = false,
            bool ignoreExpand = false,
            bool pendingExpand = false,
            int dropDownButtonCount = 0,
            int truncatedDialogRead = 0)
        {
            var directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(),
                "avdt-file-filter-timing-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            var source = typeof(NativeFileFilterTimingTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "NativeTestPaths").Value!.Split('|')
                .Single(path => Path.GetFileName(path) == "AvaloniaDevTool.NativeAutomation.Fixture.dll");
            var stem = Path.GetFileNameWithoutExtension(source);
            foreach (var suffix in new[] { ".exe", ".dll", ".runtimeconfig.json", ".deps.json" })
                File.Copy(Path.Combine(Path.GetDirectoryName(source)!, stem + suffix),
                    Path.Combine(directory, stem + suffix));
            await File.WriteAllTextAsync(Path.Combine(directory, "native-file-filter-timing.json"),
                JsonSerializer.Serialize(new
                    {
                        controlDirectory = directory, firstComboReadDelayMs = 3300, exposeOptions,
                        slowWindowsRead, omitSlowWindowsResult, corruptFirstPathWrite, ignoreExpand, pendingExpand,
                        dropDownButtonCount, truncatedDialogRead
                    },
                    JsonOptions),
                new UTF8Encoding(false),
                Token);
            NativeAutomationSession? session = null;
            try
            {
                using var target = Process.GetCurrentProcess();
                session = await NativeAutomationSession.AttachAsync(target.Id,
                    target.StartTime.ToUniversalTime(),
                    new NativeAutomationOptions
                    {
                        WorkerPath = Path.Combine(directory, stem + ".exe"), OperationTimeout = TimeSpan.FromSeconds(10)
                    },
                    Token);
                var identity = JsonSerializer.Deserialize<ProcessIdentity>(await File.ReadAllTextAsync(
                        Path.Combine(directory, "attached.json"),
                        Encoding.UTF8,
                        Token),
                    JsonOptions)!;
                Assert.Equal(Path.Combine(directory, stem + ".exe"), identity.ExecutablePath, ignoreCase: true);
                var worker = new TimingWorker(session, identity, directory);
                worker.AssertOsProcessRunning();
                return worker;
            }
            catch
            {
                if (session is not null)
                    await session.DisposeAsync();
                DeleteOwnedDirectory(directory);
                throw;
            }
        }

        public void AssertOsProcessRunning()
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            Assert.False(process.HasExited);
            AssertIdentity(process, identity);
        }

        public async Task<WorkerTrace> ReadTraceAsync() => JsonSerializer.Deserialize<WorkerTrace>(
            await File.ReadAllTextAsync(Path.Combine(directory, "trace.json"), Encoding.UTF8, Token),
            JsonOptions)!;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Session.DisposeAsync();
                var timer = Stopwatch.StartNew();
                while (!HasOsProcessExited(identity) && timer.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(20, CancellationToken.None);
                if (!HasOsProcessExited(identity))
                {
                    using var process = Process.GetProcessById(identity.ProcessId);
                    AssertIdentity(process, identity);
                    process.Kill();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(timeout.Token);
                }

                Assert.True(HasOsProcessExited(identity), "The synthetic filter worker did not exit.");
            }
            finally
            {
                DeleteOwnedDirectory(directory);
            }
        }

        private static void DeleteOwnedDirectory(string directory)
        {
            var resolved = Path.GetFullPath(directory);
            var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) +
                       Path.DirectorySeparatorChar;
            const string prefix = "avdt-file-filter-timing-";
            if (!string.Equals(Path.GetDirectoryName(resolved) + Path.DirectorySeparatorChar,
                    temp,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(Path.GetFileName(resolved)[prefix.Length..], "N", out _))
                throw new InvalidOperationException("Refusing to delete an unowned synthetic filter directory.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static bool HasOsProcessExited(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            try
            {
                if (process.HasExited)
                    return true;
                AssertIdentity(process, identity);
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

    private static void AssertIdentity(Process process, ProcessIdentity identity)
    {
        Assert.Equal(identity.StartTimeUtcTicks, process.StartTime.ToUniversalTime().Ticks);
        var length = 32768;
        var path = new StringBuilder(length);
        if (!QueryFullProcessImageName(process.SafeHandle, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Assert.Equal(identity.ExecutablePath, Path.GetFullPath(path.ToString()), ignoreCase: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process,
        uint flags,
        StringBuilder path,
        ref int size);
}
