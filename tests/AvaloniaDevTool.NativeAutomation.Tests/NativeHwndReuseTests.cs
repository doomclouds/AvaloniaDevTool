using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.NativeAutomation.Worker;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

[Collection(NativeHwndReuseCollection.Name)]
public sealed class NativeHwndReuseTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static bool IsInteractiveWindows => OperatingSystem.IsWindows() && Environment.UserInteractive;

    [Fact(SkipUnless = nameof(IsInteractiveWindows), Skip = "A real interactive Windows desktop is required.")]
    public async Task Destroying_and_recreating_real_windows_expires_the_original_root_and_edit_nodes()
    {
        await using var fixture = await HwndFixture.StartAsync();
        using var monitor = new NativeWindowLifetimeMonitor(fixture.Ready.ProcessId);
        var rootStamp = monitor.Capture(new nint(fixture.Ready.OriginalRoot), fixture.Ready.ProcessId);
        var editStamp = monitor.Capture(new nint(fixture.Ready.OriginalEdit), fixture.Ready.ProcessId);
        monitor.Validate(rootStamp);
        monitor.Validate(editStamp);
        await using var native = await fixture.AttachAsync();
        var original = await ReadNodesAsync(native, fixture.Ready);
        Assert.Equal("generation:1", original.Edit.Value);
        var destroyed = await fixture.CommandAsync(new { command = "destroy" });
        Assert.Equal("destroyed", destroyed.Status);
        Assert.Equal(0, destroyed.CurrentRoot);
        Assert.Equal(0, destroyed.CurrentEdit);
        var monitorObservation = await WaitForDestroyedStampsAsync(monitor, rootStamp, editStamp);
        Evidence("native-window-destroy-events", monitorObservation);
        var rootObservation = Assert.Single(monitorObservation, item => item.Handle == rootStamp.Handle.ToInt64());
        Assert.Equal("stale_node", rootObservation.ErrorCode);
        Assert.True(rootObservation.CurrentEpoch > rootObservation.OriginalEpoch);
        // Parent destruction covers descendants; individual child WinEvents are not guaranteed.
        // Match the production safety contract: validate the root lifetime before the child lifetime.
        var combined = Assert.Throws<NativeAutomationException>(() =>
        {
            monitor.Validate(rootStamp);
            monitor.Validate(editStamp);
        });
        Assert.Equal("stale_node", combined.Code);
        Evidence("native-window-root-then-child-validation", new { errorCode = combined.Code });
        await AssertExpiredAsync(native, original.Root.NodeId);
        await AssertExpiredAsync(native, original.Edit.NodeId);
        var recreated = await fixture.CommandAsync(new { command = "recreate" });
        Assert.Equal("recreated", recreated.Status);
        var replacement = await ReadNodesAsync(native, recreated);
        Assert.Equal(recreated.EditValue, replacement.Edit.Value);
        Assert.NotEqual(original.Root.NodeId, replacement.Root.NodeId);
        Assert.NotEqual(original.Edit.NodeId, replacement.Edit.NodeId);
        await AssertExpiredAsync(native, original.Root.NodeId);
        await AssertExpiredAsync(native, original.Edit.NodeId);
        Evidence("destroy-recreate", new { fixture.Ready, destroyed, recreated, original, replacement });
    }

    // Run this bounded acceptance experiment explicitly with: dotnet test --filter Category=NativeHwndReuse -- xUnit.Explicit=only
    [Fact(Explicit = true,
        SkipUnless = nameof(IsInteractiveWindows),
        Skip = "A real interactive Windows desktop is required.")]
    [Trait("Category", "NativeHwndReuse")]
    public async Task An_observed_exact_root_hwnd_reuse_does_not_redirect_original_native_node_ids()
    {
        await using var fixture = await HwndFixture.StartAsync();
        await using var native = await fixture.AttachAsync();
        var original = await ReadNodesAsync(native, fixture.Ready);
        using var uia = new PrivateUiaDiagnostics();
        var rootProxy = uia.FromHandle(fixture.Ready.OriginalRoot);
        var editProxy = uia.FromHandle(fixture.Ready.OriginalEdit);
        var originalRootUia = rootProxy.Read();
        var originalEditUia = editProxy.Read();
        Assert.Null(originalRootUia.Error);
        Assert.Null(originalEditUia.Error);
        Assert.Equal("generation:1", originalEditUia.Value);
        var destroyed = await fixture.CommandAsync(new { command = "destroy" });
        Assert.Equal("destroyed", destroyed.Status);
        // Do not query the old IDs during churn: the test must also cover an unobserved destruction interval.
        var reuse = await fixture.CommandAsync(new { command = "reuse", maxIterations = 2_000_000, budgetMs = 180_000 },
            TimeSpan.FromSeconds(190));
        var oldRootUia = rootProxy.Read();
        var oldEditUia = editProxy.Read();
        var replacementRootUia = uia.FromHandle(reuse.CurrentRoot).Read();
        var replacementEditUia = uia.FromHandle(reuse.CurrentEdit).Read();
        Evidence("hwnd-reuse-probe",
            new
            {
                reuse, originalRootUia, originalEditUia, oldRootUia, oldEditUia, replacementRootUia, replacementEditUia
            });
        Assert.True(reuse.Status == "reused" && reuse.RootReused && reuse.CurrentRoot == fixture.Ready.OriginalRoot,
            $"No exact root HWND reuse was observed within the bounded probe: {JsonSerializer.Serialize(reuse, JsonOptions)}");
        Assert.True(reuse.Generation > fixture.Ready.Generation);
        Assert.True(reuse.OriginalSlotVisits > 0);
        Assert.InRange(reuse.BlockersRetained, 0, 32);
        Assert.Equal(0, reuse.BlockersRemaining);
        Assert.InRange(reuse.PeakOwnedWindows, 2, 34);
        Assert.NotEqual(fixture.Ready.EditValue, reuse.EditValue);
        Assert.Equal(fixture.Ready.ProcessId, reuse.ProcessId);
        Assert.Equal(fixture.Ready.StartTimeUtc, reuse.StartTimeUtc);

        // Capture both old proxies before fresh enumeration can update the registry's wrappers.
        var oldRootBefore = await ObserveAsync(native, original.Root.NodeId);
        var oldEditBefore = await ObserveAsync(native, original.Edit.NodeId);
        var replacement = await ReadNodesAsync(native, reuse);
        var oldRootAfter = await ObserveAsync(native, original.Root.NodeId);
        var oldEditAfter = await ObserveAsync(native, original.Edit.NodeId);
        var oldEditAction = await ObserveSetValueAsync(native, original.Edit.NodeId);
        var afterOldEditAction = await fixture.CommandAsync(new { command = "state" });
        Evidence("hwnd-reuse-native-identities",
            new
            {
                original, replacement, oldRootBefore, oldEditBefore, oldRootAfter, oldEditAfter, oldEditAction,
                afterOldEditAction
            });
        AssertExpired(oldRootBefore);
        AssertExpired(oldEditBefore);
        AssertExpired(oldRootAfter);
        AssertExpired(oldEditAfter);
        Assert.NotEqual(original.Root.NodeId, replacement.Root.NodeId);
        Assert.NotEqual(original.Edit.NodeId, replacement.Edit.NodeId);
        Assert.Equal(reuse.EditValue, replacement.Edit.Value);
        Assert.True(oldEditAction.ErrorCode is "stale_node" or "out_of_scope",
            JsonSerializer.Serialize(oldEditAction, JsonOptions));
        Assert.Equal(reuse.EditValue, afterOldEditAction.EditValue);
        Assert.Equal(reuse.EditValue,
            (await native.GetElementAsync(replacement.Edit.NodeId, TestContext.Current.CancellationToken)).Value);
        // The broken baseline must fail above before any stale-root focus attempt can run.
        var oldRootFocus = await ObserveActionAsync(native,
            new NativeActionRequest { NodeId = original.Root.NodeId, Action = "Focus" });
        Evidence("hwnd-reuse-stale-root-focus", oldRootFocus);
        Assert.True(oldRootFocus.ErrorCode is "stale_node" or "out_of_scope",
            JsonSerializer.Serialize(oldRootFocus, JsonOptions));
        await AssertExpiredAsync(native, original.Root.NodeId);
        await AssertExpiredAsync(native, original.Edit.NodeId);
    }

    private static async Task<NativeNodes> ReadNodesAsync(NativeAutomationSession native, FixtureState state)
    {
        var root = Assert.Single(await native.GetWindowsAsync(TestContext.Current.CancellationToken),
            item => item.Handle == state.CurrentRoot);
        Assert.Equal(state.ProcessId, root.ProcessId);
        Assert.Equal(FormattableString.Invariant($"AVDT HWND Cycle {state.Generation}"), root.Title);
        var tree = await native.GetTreeAsync(
            new NativeTreeQuery { RootNodeId = root.NodeId, MaxDepth = 4, MaxNodes = 20 },
            TestContext.Current.CancellationToken);
        Assert.False(tree.Truncated);
        var edit = Assert.Single(tree.Nodes, item => item.NativeWindowHandle == state.CurrentEdit);
        Assert.Equal(state.ProcessId, edit.ProcessId);
        Assert.Equal("Edit", edit.ControlType);
        return new(root, edit);
    }

    private static async Task AssertExpiredAsync(NativeAutomationSession native, string id) =>
        AssertExpired(await ObserveAsync(native, id));

    private static void AssertExpired(NodeObservation observation) =>
        Assert.True(observation.ErrorCode is "stale_node" or "out_of_scope",
            JsonSerializer.Serialize(observation, JsonOptions));

    private static async Task<MonitorObservation[]> WaitForDestroyedStampsAsync(NativeWindowLifetimeMonitor monitor,
        params NativeWindowLifetimeMonitor.Stamp[] stamps)
    {
        var timer = Stopwatch.StartNew();
        var errors = new string?[stamps.Length];
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            for (var index = 0; index < stamps.Length; index++)
            {
                try
                {
                    monitor.Validate(stamps[index]);
                }
                catch (NativeAutomationException exception)
                {
                    errors[index] = exception.Code;
                }
            }

            if (errors[0] == "stale_node")
                break;
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        return stamps.Select((stamp, index) => new MonitorObservation(stamp.Handle.ToInt64(),
            stamp.Epoch,
            monitor.Capture(stamp.Handle, stamp.ProcessId).Epoch,
            errors[index],
            timer.ElapsedMilliseconds)).ToArray();
    }

    private static async Task<NodeObservation> ObserveAsync(NativeAutomationSession native, string id)
    {
        try
        {
            return new(id, await native.GetElementAsync(id, TestContext.Current.CancellationToken), null);
        }
        catch (NativeAutomationException exception)
        {
            return new(id, null, exception.Code);
        }
    }

    private static Task<ActionObservation> ObserveSetValueAsync(NativeAutomationSession native, string id) =>
        ObserveActionAsync(native,
            new NativeActionRequest { NodeId = id, Action = "SetValue", Value = "must-not-reach-replacement" });

    private static async Task<ActionObservation> ObserveActionAsync(NativeAutomationSession native,
        NativeActionRequest request)
    {
        try
        {
            var result = await native.ExecuteAsync(request, TestContext.Current.CancellationToken);
            var timer = Stopwatch.StartNew();
            while (result.Status == "pending" && result.OperationId is { } operation &&
                   timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
                result = await native.GetActionStatusAsync(operation, TestContext.Current.CancellationToken);
            }

            return new(result.Status, result.ErrorCode);
        }
        catch (NativeAutomationException exception)
        {
            return new("rejected", exception.Code);
        }
    }

    private static void Evidence(string evidence, object actual) => TestContext.Current.TestOutputHelper?.WriteLine(
        JsonSerializer.Serialize(new { evidence, actual }, JsonOptions));

    private sealed record NativeNodes(NativeWindowInfo Root, NativeElementInfo Edit);

    private sealed record NodeObservation(string NodeId, NativeElementInfo? Element, string? ErrorCode);

    private sealed record MonitorObservation(
        long Handle,
        long OriginalEpoch,
        long CurrentEpoch,
        string? ErrorCode,
        long ElapsedMs);

    private sealed record ActionObservation(string Status, string? ErrorCode);

    private sealed record UiaObservation(string? RuntimeKey, string? Name, string? Value, string? Error, int HResult);

    private sealed record FixtureState(
        string Status,
        int ProcessId,
        DateTimeOffset StartTimeUtc,
        long OriginalRoot,
        long OriginalEdit,
        long CurrentRoot,
        long CurrentEdit,
        bool RootReused,
        bool EditReused,
        int Generation,
        string EditValue,
        int CreatedWindows,
        int Iterations,
        long ElapsedMs,
        int DistinctIndices,
        int OriginalSlotVisits,
        int BlockersRetained,
        int BlockersRemaining,
        int PeakOwnedWindows,
        long WorkingSetBytes,
        int KernelHandles,
        uint UserObjects,
        string? BudgetReason);

    // Private diagnostics load the already-built provider dependencies without changing the public contracts.
    private sealed class PrivateUiaDiagnostics : IDisposable
    {
        private readonly ProviderLoadContext
            _context = new(ArtifactPath("AvaloniaDevTool.NativeAutomation.Worker.dll"));

        private readonly dynamic _automation;

        internal PrivateUiaDiagnostics()
        {
            var path = Path.Combine(Path.GetDirectoryName(ArtifactPath("AvaloniaDevTool.NativeAutomation.Worker.dll"))!,
                "FlaUI.UIA3.dll");
            var assembly = _context.LoadFromAssemblyPath(path);
            _automation = Activator.CreateInstance(assembly.GetType("FlaUI.UIA3.UIA3Automation", throwOnError: true)!)!;
            _automation.ConnectionTimeout = TimeSpan.FromSeconds(2);
            _automation.TransactionTimeout = TimeSpan.FromSeconds(3);
        }

        internal UiaProxy FromHandle(long handle) => new(_automation.FromHandle(new nint(handle)));

        public void Dispose()
        {
            ((IDisposable)_automation).Dispose();
            _context.Unload();
        }
    }

    private sealed class UiaProxy(object element)
    {
        internal UiaObservation Read()
        {
            string? runtimeKey = null;
            string? name = null;
            string? value = null;
            try
            {
                var properties = Property(element, "Properties")!;
                runtimeKey = string.Join('.', (int[])Property(Property(properties, "RuntimeId")!, "Value")!);
                name = (string?)Property(Property(properties, "Name")!, "Value");
                var valuePattern = Property(Property(Property(element, "Patterns")!, "Value")!, "PatternOrDefault");
                if (valuePattern is not null)
                    value = (string?)Property(Property(valuePattern, "Value")!, "Value");
                return new(runtimeKey, name, value, null, 0);
            }
            catch (Exception exception)
            {
                while (exception is TargetInvocationException { InnerException: { } inner })
                    exception = inner;
                return new(runtimeKey,
                    name,
                    value,
                    exception.GetType().FullName + ": " + exception.Message,
                    exception.HResult);
            }
        }

        // FlaUI implements some pattern accessors explicitly; use their actual CLR interface properties.
        private static object? Property(object owner, string name)
        {
            var type = owner.GetType();
            var property = type.GetProperty(name) ?? type.GetInterfaces().Select(item => item.GetProperty(name))
                    .FirstOrDefault(item => item is not null)
                ?? throw new MissingMemberException(type.FullName, name);
            return property.GetValue(owner);
        }
    }

    private sealed class ProviderLoadContext(string providerPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(providerPath);

        protected override Assembly? Load(AssemblyName assemblyName) =>
            _resolver.ResolveAssemblyToPath(assemblyName) is { } path
                ? LoadFromAssemblyPath(path)
                : null;
    }

    private sealed class HwndFixture(Process process, Task<string> standardError) : IAsyncDisposable
    {
        internal FixtureState Ready { get; private set; } = null!;

        internal static async Task<HwndFixture> StartAsync()
        {
            var start = new ProcessStartInfo(
                Path.ChangeExtension(ArtifactPath("AvaloniaDevTool.NativeAutomation.Fixture.dll"), ".exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add("--native-hwnd-reuse");
            var process = Process.Start(start) ??
                          throw new InvalidOperationException("The HWND fixture could not start.");
            var fixture = new HwndFixture(process, process.StandardError.ReadToEndAsync());
            try
            {
                fixture.Ready = await fixture.ReadAsync(TimeSpan.FromSeconds(15));
                Assert.Equal("ready", fixture.Ready.Status);
                Assert.Equal(process.Id, fixture.Ready.ProcessId);
                Assert.Equal(process.StartTime.ToUniversalTime(), fixture.Ready.StartTimeUtc.UtcDateTime);
                Assert.NotEqual(0, fixture.Ready.OriginalRoot);
                Assert.NotEqual(0, fixture.Ready.OriginalEdit);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal Task<NativeAutomationSession> AttachAsync() => NativeAutomationSession.AttachAsync(
            Ready.ProcessId,
            Ready.StartTimeUtc,
            new NativeAutomationOptions { WorkerPath = ArtifactPath("AvaloniaDevTool.NativeAutomation.Worker.dll") },
            TestContext.Current.CancellationToken);

        internal async Task<FixtureState> CommandAsync(object command, TimeSpan? timeout = null)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command, JsonOptions));
            await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
            return await ReadAsync(timeout ?? TimeSpan.FromSeconds(15));
        }

        private async Task<FixtureState> ReadAsync(TimeSpan timeout)
        {
            using var cancellation =
                CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(timeout);
            var line = await process.StandardOutput.ReadLineAsync(cancellation.Token);
            if (line is null)
                throw new InvalidOperationException("The HWND fixture exited: " + await standardError);
            return JsonSerializer.Deserialize<FixtureState>(line, JsonOptions) ??
                   throw new InvalidOperationException("The HWND fixture returned no state.");
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.StandardInput.Close();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }

                var errors = await standardError;
                if (!string.IsNullOrWhiteSpace(errors))
                    TestContext.Current.TestOutputHelper?.WriteLine(errors);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string ArtifactPath(string filename) => typeof(NativeHwndReuseTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "NativeTestPaths").Value!.Split('|')
        .Single(path => Path.GetFileName(path) == filename);
}

[CollectionDefinition(NativeHwndReuseCollection.Name, DisableParallelization = true)]
public sealed class NativeHwndReuseCollection
{
    public const string Name = "Native HWND reuse exclusive desktop";
}
