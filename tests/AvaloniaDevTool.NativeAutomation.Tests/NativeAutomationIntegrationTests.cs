using System.Diagnostics;
using System.Reflection;
using AvaloniaDevTool.NativeAutomation;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed partial class NativeAutomationIntegrationTests
{
    public static bool IsInteractiveWindows => OperatingSystem.IsWindows() && Environment.UserInteractive;

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_patterns_owned_modals_scope_and_stale_session_ids_are_real()
    {
        using var fixture = await StartFixtureAsync();
        await using var native = await AttachAsync(fixture);
        var roots = await native.GetWindowsAsync(TestContext.Current.CancellationToken);
        var main = Assert.Single(roots, root => root.Title == "AVDT Native Scope Fixture");
        Assert.True(main.IsEnabled);
        Assert.All(roots, root => Assert.Equal(fixture.Id, root.ProcessId));
        var bounded = await native.GetTreeAsync(
            new NativeTreeQuery { RootNodeId = main.NodeId, MaxNodes = 2, MaxDepth = 8 },
            TestContext.Current.CancellationToken);
        Assert.True(bounded.Truncated);
        Assert.Equal(2, bounded.Nodes.Length);
        var editor = await FindAsync(native, "NativeEditor");
        Assert.Equal("initial", editor.Value);
        Assert.Equal("completed",
            (await CompleteAsync(native,
                new NativeActionRequest { NodeId = editor.NodeId, Action = "SetValue", Value = "changed 中文" })).Status);
        Assert.Equal("changed 中文",
            (await native.GetElementAsync(editor.NodeId, TestContext.Current.CancellationToken)).Value);
        var toggle = await FindAsync(native, "NativeToggle");
        await CompleteAsync(native, new NativeActionRequest { NodeId = toggle.NodeId, Action = "Toggle" });
        Assert.Equal("On",
            (await native.GetElementAsync(toggle.NodeId, TestContext.Current.CancellationToken)).ToggleState);
        var invoke = await FindAsync(native, "NativeInvoke");
        await CompleteAsync(native, new NativeActionRequest { NodeId = invoke.NodeId, Action = "Invoke" });
        await WaitForStatusAsync(native, "invoked:1");
        var unsupported =
            await CompleteAsync(native, new NativeActionRequest { NodeId = editor.NodeId, Action = "Toggle" });
        Assert.Equal("failed", unsupported.Status);
        Assert.Equal("unsupported_pattern", unsupported.ErrorCode);
        var modalButton = await FindAsync(native, "NativeModal");
        var modalAction = await native.ExecuteAsync(
            new NativeActionRequest { NodeId = modalButton.NodeId, Action = "Invoke" },
            TestContext.Current.CancellationToken);
        Assert.True(modalAction.Status == "dispatched",
            $"Modal HWND={modalButton.NativeWindowHandle}, class={modalButton.ClassName}, action={modalAction.Status}");
        var owned = await WaitForWindowAsync(native, "AVDT Owned Modal");
        Assert.Equal(main.Handle, owned.OwnerHandle);
        Assert.True(owned.IsOwned);
        Assert.True(owned.IsModal);
        Assert.False((await native.GetWindowsAsync(TestContext.Current.CancellationToken))
            .Single(window => window.NodeId == main.NodeId).IsEnabled);
        var disabled = await Assert.ThrowsAsync<NativeAutomationException>(() =>
            native.ExecuteAsync(new NativeActionRequest
                    { NodeId = invoke.NodeId, Action = "Invoke" },
                TestContext.Current.CancellationToken));
        Assert.Equal("element_disabled", disabled.Code);
        Assert.Equal("invoked:1", (await FindAsync(native, "NativeStatus")).Name);
        var blockedInput = await Assert.ThrowsAsync<NativeAutomationException>(() =>
            native.SendInputAsync(new NativeInputRequest
                    { NodeId = editor.NodeId, Kind = "Text", Text = "must not reach disabled owner" },
                TestContext.Current.CancellationToken));
        Assert.Equal("element_not_interactive", blockedInput.Code);
        Assert.Equal("changed 中文",
            (await native.GetElementAsync(editor.NodeId, TestContext.Current.CancellationToken)).Value);
        var close = await FindAsync(native, "NativeModalClose");
        await CompleteAsync(native, new NativeActionRequest { NodeId = close.NodeId, Action = "Invoke" });
        if (modalAction.OperationId is { } operationId)
            Assert.Equal("completed", (await WaitForOperationAsync(native, operationId)).Status);
        var old = await Assert.ThrowsAsync<NativeAutomationException>(() =>
            native.GetElementAsync(close.NodeId, TestContext.Current.CancellationToken));
        Assert.True(old.Code is "stale_node" or "out_of_scope", $"{old.Code}: {old.Message}");
        await using var fresh = await AttachAsync(fixture);
        var crossSession = await Assert.ThrowsAsync<NativeAutomationException>(() =>
            fresh.GetElementAsync(editor.NodeId, TestContext.Current.CancellationToken));
        Assert.Equal("stale_node", crossSession.Code);
        var wrongIdentity = await Assert.ThrowsAsync<NativeAutomationException>(() =>
            NativeAutomationSession.AttachAsync(
                fixture.Id,
                new DateTimeOffset(fixture.StartTime.ToUniversalTime()).AddSeconds(1),
                Options(),
                TestContext.Current.CancellationToken));
        Assert.Equal("target_changed", wrongIdentity.Code);
        await native.DisposeAsync();
        Assert.False(native.IsAlive);
        Assert.Equal("session_expired",
            (await Assert.ThrowsAsync<NativeAutomationException>(() =>
                native.GetWindowsAsync(TestContext.Current.CancellationToken))).Code);
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task A_worker_timeout_is_bounded_and_a_new_session_can_attach()
    {
        using var fixture = await StartFixtureAsync();
        var timer = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<NativeAutomationException>(() => NativeAutomationSession.AttachAsync(
            fixture.Id,
            fixture.StartTime.ToUniversalTime(),
            Options() with { OperationTimeout = TimeSpan.FromMilliseconds(1) },
            TestContext.Current.CancellationToken));
        Assert.Equal("operation_timeout", error.Code);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5));
        await using var fresh = await AttachAsync(fixture);
        Assert.NotEmpty(await fresh.GetWindowsAsync(TestContext.Current.CancellationToken));
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_input_waits_for_activation_when_alternating_between_scoped_windows()
    {
        using var fixture = await StartFixtureAsync();
        await using var native = await AttachAsync(fixture);
        var primary = await FindAsync(native, "NativeEditor");
        var open = await FindAsync(native, "NativeSecondary");
        await CompleteAsync(native, new NativeActionRequest { NodeId = open.NodeId, Action = "Invoke" });
        await WaitForWindowAsync(native, "AVDT Secondary Window");
        var secondary = await FindAsync(native, "NativeSecondaryEditor");
        var errors = new List<string>();
        var count = 0;
        for (var cycle = 0; cycle < 40; cycle++)
        {
            foreach (var node in new[] { primary, secondary })
            {
                try
                {
                    var result = await native.SendInputAsync(new NativeInputRequest
                            { NodeId = node.NodeId, Kind = "KeyChord", Modifiers = ["Ctrl"], Keys = ["A"] },
                        TestContext.Current.CancellationToken);
                    Assert.True(result.Injected);
                    var text = $"{(node.NodeId == primary.NodeId ? "primary" : "secondary")}-{cycle}-焦点 😀";
                    Assert.True((await native.SendInputAsync(new NativeInputRequest
                            { NodeId = node.NodeId, Kind = "Text", Text = text },
                        TestContext.Current.CancellationToken)).Injected);
                    var timer = Stopwatch.StartNew();
                    while ((await native.GetElementAsync(node.NodeId, TestContext.Current.CancellationToken)).Value !=
                           text && timer.Elapsed < TimeSpan.FromSeconds(5))
                        await Task.Delay(10, TestContext.Current.CancellationToken);
                    Assert.Equal(text,
                        (await native.GetElementAsync(node.NodeId, TestContext.Current.CancellationToken)).Value);
                    count++;
                }
                catch (NativeAutomationException exception)
                {
                    errors.Add($"{exception.Code}: {exception.Message}");
                }
            }
        }

        Assert.True(errors.Count == 0,
            $"Accepted {count}/80 scoped activations. Errors: {string.Join(" | ", errors.Take(8))}");
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Native_keyboard_input_refuses_a_third_owned_root_that_interrupts_target_focus()
    {
        using var fixture = await StartFixtureAsync();
        await using var native = await AttachAsync(fixture);
        var primary = await FindAsync(native, "NativeEditor");
        await CompleteAsync(native,
            new NativeActionRequest
                { NodeId = (await FindAsync(native, "NativeSecondary")).NodeId, Action = "Invoke" });
        await WaitForWindowAsync(native, "AVDT Secondary Window");
        await CompleteAsync(native,
            new NativeActionRequest
                { NodeId = (await FindAsync(native, "NativeEnableFocusInterruption")).NodeId, Action = "Invoke" });
        var interrupter = await WaitForWindowAsync(native, "AVDT Focus Interrupter");
        Assert.Equal(fixture.Id, interrupter.ProcessId);
        Assert.True(interrupter.IsOwned);
        var thirdEditor = await FindAsync(native, "NativeInterrupterEditor");
        Assert.True((await native.SendInputAsync(new NativeInputRequest
                { NodeId = thirdEditor.NodeId, Kind = "KeyChord", Keys = ["End"] },
            TestContext.Current.CancellationToken)).Injected);
        Assert.True((await native.GetElementAsync(thirdEditor.NodeId, TestContext.Current.CancellationToken))
            .HasKeyboardFocus);
        var error = await Assert.ThrowsAsync<NativeAutomationException>(() =>
            native.SendInputAsync(new NativeInputRequest
                    { NodeId = primary.NodeId, Kind = "Text", Text = "must not reach another owned window" },
                TestContext.Current.CancellationToken));
        Assert.Equal("foreground_changed", error.Code);
        Assert.Equal("initial",
            (await native.GetElementAsync(primary.NodeId, TestContext.Current.CancellationToken)).Value);
        Assert.Equal("untouched",
            (await native.GetElementAsync(thirdEditor.NodeId, TestContext.Current.CancellationToken)).Value);
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Closed_nodes_keep_precise_error_classification_during_repeated_modal_close_races()
    {
        using var fixture = await StartFixtureAsync();
        await using var native = await AttachAsync(fixture);
        var opener = await FindAsync(native, "NativeModal");
        var errors = new List<string>();
        for (var cycle = 0; cycle < 30; cycle++)
        {
            await CompleteAsync(native, new NativeActionRequest { NodeId = opener.NodeId, Action = "Invoke" });
            var owned = await WaitForWindowAsync(native, "AVDT Owned Modal");
            var close = await FindAsync(native, "NativeModalClose");
            await CompleteAsync(native, new NativeActionRequest { NodeId = close.NodeId, Action = "Invoke" });
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                try
                {
                    await native.GetElementAsync(close.NodeId, TestContext.Current.CancellationToken);
                }
                catch (NativeAutomationException exception)
                {
                    if (exception.Code is not ("stale_node" or "out_of_scope"))
                        errors.Add($"Cycle {cycle}: {exception.Code}: {exception.Message}");
                    break;
                }
            }

            while ((await native.GetWindowsAsync(TestContext.Current.CancellationToken)).Any(window =>
                       window.NodeId == owned.NodeId) && timer.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(5, TestContext.Current.CancellationToken);
            Assert.DoesNotContain(await native.GetWindowsAsync(TestContext.Current.CancellationToken),
                window => window.NodeId == owned.NodeId);
            var closed = await Assert.ThrowsAsync<NativeAutomationException>(() =>
                native.GetElementAsync(close.NodeId, TestContext.Current.CancellationToken));
            Assert.True(closed.Code is "stale_node" or "out_of_scope", $"{closed.Code}: {closed.Message}");
        }

        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Real_os_input_uses_scoped_element_coordinates_and_unicode_with_application_readback()
    {
        using var fixture = await StartFixtureAsync();
        await using var native = await AttachAsync(fixture);
        var editor = await FindAsync(native, "NativeEditor");
        var select = await native.SendInputAsync(new NativeInputRequest
                { NodeId = editor.NodeId, Kind = "KeyChord", Modifiers = ["Ctrl"], Keys = ["A"] },
            TestContext.Current.CancellationToken);
        Assert.True(select.Injected);
        Assert.Equal(4, select.InputCount);
        var text = await native.SendInputAsync(new NativeInputRequest
                { NodeId = editor.NodeId, Kind = "Text", Text = "原生 Unicode 😀" },
            TestContext.Current.CancellationToken);
        Assert.True(text.Injected);
        Assert.False(text.ApplicationEffectVerified);
        var timer = Stopwatch.StartNew();
        while ((await native.GetElementAsync(editor.NodeId, TestContext.Current.CancellationToken)).Value !=
               "原生 Unicode 😀" && timer.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal("原生 Unicode 😀",
            (await native.GetElementAsync(editor.NodeId, TestContext.Current.CancellationToken)).Value);
        var invoke = await FindAsync(native, "NativeInvoke");
        var click = await native.SendInputAsync(new NativeInputRequest { NodeId = invoke.NodeId, Kind = "Click" },
            TestContext.Current.CancellationToken);
        Assert.True(click.Injected);
        Assert.Equal(3, click.InputCount);
        await WaitForStatusAsync(native, "invoked:1");
        var pointer = await FindAsync(native, "NativePointer");
        Assert.True((await native.SendInputAsync(
            new NativeInputRequest { NodeId = pointer.NodeId, Kind = "RightClick" },
            TestContext.Current.CancellationToken)).Injected);
        await WaitForStatusAsync(native, "pointer:right");
        Assert.True((await native.SendInputAsync(
            new NativeInputRequest { NodeId = pointer.NodeId, Kind = "DoubleClick" },
            TestContext.Current.CancellationToken)).Injected);
        await WaitForStatusAsync(native, "pointer:double");
        Assert.True((await native.SendInputAsync(
            new NativeInputRequest { NodeId = pointer.NodeId, Kind = "Move", X = 10, Y = 10 },
            TestContext.Current.CancellationToken)).Injected);
        var drag = await native.SendInputAsync(new NativeInputRequest
            {
                NodeId = pointer.NodeId, Kind = "Drag", X = 10, Y = 10, TargetNodeId = pointer.NodeId, TargetX = 300,
                TargetY = 40
            },
            TestContext.Current.CancellationToken);
        Assert.True(drag.Injected);
        Assert.Equal(15, drag.InputCount);
        Assert.True((await native.SendInputAsync(
            new NativeInputRequest { NodeId = pointer.NodeId, Kind = "Wheel", WheelDelta = 120 },
            TestContext.Current.CancellationToken)).Injected);
        var coordinates = await Assert.ThrowsAsync<NativeAutomationException>(() =>
            native.SendInputAsync(new NativeInputRequest
                    { NodeId = pointer.NodeId, Kind = "Click", X = -1, Y = 0 },
                TestContext.Current.CancellationToken));
        Assert.Equal("invalid_coordinates", coordinates.Code);
        var global = await Assert.ThrowsAsync<NativeAutomationException>(() =>
            native.SendInputAsync(new NativeInputRequest
                    { NodeId = editor.NodeId, Kind = "KeyChord", Modifiers = ["Alt"], Keys = ["Tab"] },
                TestContext.Current.CancellationToken));
        Assert.Equal("out_of_scope", global.Code);
    }

    private static async Task WaitForStatusAsync(NativeAutomationSession native, string status)
    {
        var timer = Stopwatch.StartNew();
        while ((await FindAsync(native, "NativeStatus")).Name != status && timer.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(status, (await FindAsync(native, "NativeStatus")).Name);
    }

    private static NativeAutomationOptions Options() => new()
        { WorkerPath = ArtifactPath("AvaloniaDevTool.NativeAutomation.Worker.dll") };

    private static Task<NativeAutomationSession> AttachAsync(Process fixture) => NativeAutomationSession.AttachAsync(
        fixture.Id,
        fixture.StartTime.ToUniversalTime(),
        Options(),
        TestContext.Current.CancellationToken);

    private static string ArtifactPath(string filename) => typeof(NativeAutomationIntegrationTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "NativeTestPaths").Value!.Split('|')
        .Single(path => Path.GetFileName(path) == filename);

    private static async Task<Process> StartFixtureAsync()
    {
        var start = new ProcessStartInfo("dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(ArtifactPath("AvaloniaDevTool.NativeAutomation.Fixture.dll"));
        var process = new OwnedProcess(start);
        process.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            Assert.NotNull(await process.StandardOutput.ReadLineAsync(timeout.Token));
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static async Task<NativeElementInfo> FindAsync(NativeAutomationSession native, string automationId) =>
        Assert.Single((await native.FindAsync(new NativeFindQuery { AutomationId = automationId })).Matches);

    private static async Task<NativeWindowInfo> WaitForWindowAsync(NativeAutomationSession native, string title)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            var window = (await native.GetWindowsAsync()).SingleOrDefault(item => item.Title == title);
            if (window is not null)
                return window;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Native window '{title}' did not appear.");
    }

    private static async Task<NativeActionResult> CompleteAsync(NativeAutomationSession native,
        NativeActionRequest request)
    {
        var result = await native.ExecuteAsync(request);
        return result.Status == "pending" ? await WaitForOperationAsync(native, result.OperationId!) : result;
    }

    private static async Task<NativeActionResult> WaitForOperationAsync(NativeAutomationSession native,
        string operationId)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(10))
        {
            var result = await native.GetActionStatusAsync(operationId);
            if (result.Status != "pending")
                return result;
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("Native action did not finish.");
    }

    private sealed class OwnedProcess(ProcessStartInfo startInfo) : Process
    {
        public new bool Start()
        {
            StartInfo = startInfo;
            return base.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (!HasExited)
                        Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            base.Dispose(disposing);
        }
    }
}
