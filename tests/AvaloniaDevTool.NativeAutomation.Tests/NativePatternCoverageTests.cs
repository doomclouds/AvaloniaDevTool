using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AvaloniaDevTool.NativeAutomation;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed partial class NativeAutomationIntegrationTests
{
    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_scope_excludes_a_real_unowned_external_fixture_process()
    {
        using var target = await StartFixtureAsync();
        using var external = await StartFixtureAsync();
        await using var native = await AttachAsync(target);
        await using var externalNative = await AttachAsync(external);
        var externalWindow = Assert.Single(await externalNative.GetWindowsAsync(TestContext.Current.CancellationToken), window => window.Title == "AVDT Native Scope Fixture");
        Assert.Equal(0, externalWindow.OwnerHandle);
        Assert.False(externalWindow.IsOwned);
        Assert.Equal(external.Id, externalWindow.ProcessId);
        var windows = await native.GetWindowsAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(windows);
        Assert.All(windows, window => Assert.Equal(target.Id, window.ProcessId));
        Assert.DoesNotContain(windows, window => window.Handle == externalWindow.Handle || window.ProcessId == external.Id);
        var tree = await native.GetTreeAsync(new NativeTreeQuery { MaxDepth = 15, MaxNodes = 5000 }, TestContext.Current.CancellationToken);
        Assert.False(tree.Truncated);
        Assert.All(tree.Nodes, node => Assert.Equal(target.Id, node.ProcessId));
        Assert.DoesNotContain(tree.Nodes, node => node.NativeWindowHandle == externalWindow.Handle);
        Assert.All((await native.FindAsync(new NativeFindQuery { AutomationId = "NativeEditor" }, TestContext.Current.CancellationToken)).Matches,
            node => Assert.Equal(target.Id, node.ProcessId));
        PatternEvidence("scope", new { targetPid = target.Id, externalPid = external.Id, externalWindow.Handle, externalWindow.OwnerHandle, scopedWindows = windows.Length, scopedTreeNodes = tree.Nodes.Length });
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_password_metadata_and_patterns_are_real_and_secret_values_are_redacted()
    {
        using var controls = new ControlledNativeActions();
        using var fixture = await StartPatternFixtureAsync(controls.Prefix);
        await using var native = await AttachAsync(fixture);
        await WaitForWindowAsync(native, "AVDT Native Patterns Fixture");
        var password = await FindAsync(native, "NativePassword");
        Assert.True(password.IsPassword);
        Assert.Null(password.Value);
        AssertPattern(password, "Value");
        var editor = await FindAsync(native, "NativeEditor");
        Assert.False(editor.IsPassword);
        Assert.Equal("initial", editor.Value);
        AssertPattern(editor, "Value");
        AssertPattern(await FindAsync(native, "NativeToggle"), "Toggle");
        AssertPattern(await FindAsync(native, "NativeInvoke"), "Invoke");
        var main = Assert.Single(await native.GetWindowsAsync(TestContext.Current.CancellationToken), window => window.Title == "AVDT Native Scope Fixture");
        AssertPattern(await native.GetElementAsync(main.NodeId, TestContext.Current.CancellationToken), "Window");
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(await native.GetTreeAsync(new NativeTreeQuery { MaxDepth = 15, MaxNodes = 5000 }, TestContext.Current.CancellationToken)), StringComparison.Ordinal);
        PatternEvidence("password", new { fixturePid = fixture.Id, password.IsPassword, password.Value, password.SupportedPatterns, editorPatterns = editor.SupportedPatterns });
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_scroll_pattern_changes_the_real_list_top_index()
    {
        using var controls = new ControlledNativeActions();
        using var fixture = await StartPatternFixtureAsync(controls.Prefix);
        await using var native = await AttachAsync(fixture);
        await WaitForWindowAsync(native, "AVDT Native Patterns Fixture");
        var scroll = await FindAsync(native, "NativeScroll");
        AssertPattern(scroll, "Scroll");
        await WaitForPatternStatusAsync(native, "NativeScrollStatus", "top:0");
        var forward = await CompleteAsync(native, new NativeActionRequest { NodeId = scroll.NodeId, Action = "Scroll", VerticalScroll = "LargeIncrement" });
        Assert.Equal("completed", forward.Status);
        var timer = Stopwatch.StartNew();
        NativeElementInfo state;
        do
        {
            state = await FindAsync(native, "NativeScrollStatus");
            if (state.Name != "top:0") break;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        } while (timer.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(int.TryParse(state.Name.AsSpan(4), out var top) && top > 0, state.Name);
        var backward = await CompleteAsync(native, new NativeActionRequest { NodeId = scroll.NodeId, Action = "Scroll", VerticalScroll = "LargeDecrement" });
        Assert.Equal("completed", backward.Status);
        await WaitForPatternStatusAsync(native, "NativeScrollStatus", "top:0");
        PatternEvidence("scroll", new { fixturePid = fixture.Id, initialTopIndex = 0, forwardTopIndex = top, restoredTopIndex = 0, scroll.SupportedPatterns });
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_window_minimize_maximize_and_restore_have_provider_and_os_readback()
    {
        using var fixture = await StartFixtureAsync();
        await using var native = await AttachAsync(fixture);
        var main = Assert.Single(await native.GetWindowsAsync(TestContext.Current.CancellationToken), window => window.Title == "AVDT Native Scope Fixture");
        AssertPattern(await native.GetElementAsync(main.NodeId, TestContext.Current.CancellationToken), "Window");
        foreach (var (action, expected) in new[] { ("Minimize", "Minimized"), ("Restore", "Normal"), ("Maximize", "Maximized"), ("Restore", "Normal") })
        {
            Assert.Equal("completed", (await CompleteAsync(native, new NativeActionRequest { NodeId = main.NodeId, Action = action })).Status);
            var timer = Stopwatch.StartNew();
            NativeWindowInfo current;
            do
            {
                current = (await native.GetWindowsAsync(TestContext.Current.CancellationToken)).Single(window => window.Handle == main.Handle);
                if (current.WindowVisualState == expected) break;
                await Task.Delay(20, TestContext.Current.CancellationToken);
            } while (timer.Elapsed < TimeSpan.FromSeconds(5));
            Assert.Equal(expected, current.WindowVisualState);
            Assert.Equal(expected == "Minimized", IsIconic((nint)main.Handle));
            Assert.Equal(expected == "Maximized", IsZoomed((nint)main.Handle));
            PatternEvidence("window-state", new { fixturePid = fixture.Id, action, current.WindowVisualState, isIconic = IsIconic((nint)main.Handle), isZoomed = IsZoomed((nint)main.Handle) });
        }
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_action_status_tracks_controlled_real_provider_pending_completion_and_failure()
    {
        using var controls = new ControlledNativeActions();
        using var fixture = await StartPatternFixtureAsync(controls.Prefix);
        await using var native = await AttachAsync(fixture);
        await WaitForWindowAsync(native, "AVDT Native Patterns Fixture");
        foreach (var suffix in new[] { "Complete", "Fail" })
        {
            var node = await FindAsync(native, "NativeControlled" + suffix);
            AssertPattern(node, "Invoke");
            var action = await native.ExecuteAsync(new NativeActionRequest { NodeId = node.NodeId, Action = "Invoke" }, TestContext.Current.CancellationToken);
            Assert.Equal("pending", action.Status);
            Assert.NotNull(action.OperationId);
            Assert.True(controls.Entered(suffix).WaitOne(TimeSpan.FromSeconds(2)), "The real UIA provider did not enter Invoke.");
            Assert.Equal("pending", (await native.GetActionStatusAsync(action.OperationId, TestContext.Current.CancellationToken)).Status);
            controls.Release(suffix).Set();
            var finished = await WaitForOperationAsync(native, action.OperationId);
            Assert.Equal(suffix == "Complete" ? "completed" : "failed", finished.Status);
            Assert.Equal(action.OperationId, finished.OperationId);
            Assert.Equal(node.NodeId, finished.NodeId);
            Assert.Equal("Invoke", finished.Action);
            if (suffix == "Fail")
                Assert.True(finished.ErrorCode == "access_denied", JsonSerializer.Serialize(finished));
            else
                Assert.Null(finished.ErrorCode);
            await WaitForPatternStatusAsync(native, "NativeControlledStatus", suffix == "Complete" ? "controlled:completed" : "controlled:failed");
            PatternEvidence("controlled-action", new { fixturePid = fixture.Id, initialStatus = action.Status, observedPending = true, final = finished });
        }
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_key_chords_release_ctrl_alt_and_shift_in_real_os_state()
    {
        using var controls = new ControlledNativeActions();
        using var fixture = await StartPatternFixtureAsync(controls.Prefix);
        await using var native = await AttachAsync(fixture);
        await WaitForWindowAsync(native, "AVDT Native Patterns Fixture");
        var editor = await FindAsync(native, "NativeModifierEditor");
        AssertModifiersReleased();
        await native.SendInputAsync(new NativeInputRequest { NodeId = editor.NodeId, Kind = "KeyChord", Modifiers = ["Ctrl", "Alt", "Shift"], Keys = ["F6"] }, TestContext.Current.CancellationToken);
        await WaitForPatternStatusAsync(native, "NativeModifierStatus", "key:F6;ctrl:True;alt:True;shift:True");
        AssertModifiersReleased();
        var blocked = await Assert.ThrowsAsync<NativeAutomationException>(() => native.SendInputAsync(new NativeInputRequest
        { NodeId = editor.NodeId, Kind = "KeyChord", Modifiers = ["Ctrl", "Alt", "Shift"], Keys = ["Delete"] }, TestContext.Current.CancellationToken));
        Assert.Equal("out_of_scope", blocked.Code);
        AssertModifiersReleased();
        await native.SendInputAsync(new NativeInputRequest { NodeId = editor.NodeId, Kind = "KeyChord", Keys = ["F6"] }, TestContext.Current.CancellationToken);
        await WaitForPatternStatusAsync(native, "NativeModifierStatus", "key:F6;ctrl:False;alt:False;shift:False");
        AssertModifiersReleased();
    }

    private static void AssertPattern(NativeElementInfo node, string pattern) => Assert.Contains(node.SupportedPatterns,
        value => value == pattern || value == pattern + "Pattern" || value.EndsWith("_" + pattern + "PatternId", StringComparison.Ordinal));

    private static async Task WaitForPatternStatusAsync(NativeAutomationSession native, string automationId, string expected)
    {
        var timer = Stopwatch.StartNew();
        while ((await FindAsync(native, automationId)).Name != expected && timer.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(expected, (await FindAsync(native, automationId)).Name);
    }

    private static async Task<Process> StartPatternFixtureAsync(string prefix)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(ArtifactPath("AvaloniaDevTool.NativeAutomation.Fixture.dll"));
        start.Environment["AVDT_NATIVE_PATTERN_PREFIX"] = prefix;
        var process = new OwnedProcess(start);
        process.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { Assert.NotNull(await process.StandardOutput.ReadLineAsync(timeout.Token)); return process; }
        catch { process.Dispose(); throw; }
    }

    private static void AssertModifiersReleased()
    {
        foreach (var key in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            Assert.True((ReadAsyncKeyState(key) & 0x8000) == 0, $"OS virtual key 0x{key:X2} remained held.");
        PatternEvidence("modifier-release", new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 }
            .ToDictionary(key => $"0x{key:X2}", key => (ReadAsyncKeyState(key) & 0x8000) != 0));
    }

    private static void PatternEvidence(string evidence, object actual) =>
        TestContext.Current.TestOutputHelper?.WriteLine(JsonSerializer.Serialize(new { evidence, actual }));

    private sealed class ControlledNativeActions : IDisposable
    {
        private readonly Dictionary<string, EventWaitHandle> _events = new(StringComparer.Ordinal);
        internal string Prefix { get; } = "Local\\AVDT-native-pattern-" + Guid.NewGuid().ToString("N");
        internal ControlledNativeActions()
        {
            foreach (var suffix in new[] { "Complete", "Fail" })
            foreach (var kind in new[] { "entered", "release" })
                _events.Add(suffix + "-" + kind, new EventWaitHandle(false, EventResetMode.ManualReset, Prefix + "-" + suffix + "-" + kind));
        }
        internal EventWaitHandle Entered(string suffix) => _events[suffix + "-entered"];
        internal EventWaitHandle Release(string suffix) => _events[suffix + "-release"];
        public void Dispose()
        {
            foreach (var item in _events.Values) { item.Set(); item.Dispose(); }
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")] private static extern short ReadAsyncKeyState(int key);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsZoomed(nint window);
}
