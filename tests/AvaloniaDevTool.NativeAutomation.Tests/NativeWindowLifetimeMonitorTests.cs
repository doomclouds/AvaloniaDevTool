using AvaloniaDevTool.NativeAutomation.Worker;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed class NativeWindowLifetimeMonitorTests
{
    [Fact]
    public void Native_source_installs_and_disposes_a_process_hook_without_creating_a_window()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var monitor = new NativeWindowLifetimeMonitor(Environment.ProcessId);
    }

    [Fact]
    public void Delivered_destroy_permanently_invalidates_old_stamps_but_other_windows_remain_current()
    {
        var events = new TestEvents();
        using var monitor = new NativeWindowLifetimeMonitor(events);
        var first = monitor.Capture(42, 100);
        var other = monitor.Capture(43, 100);
        events.Destroy(42);
        Assert.Equal("stale_node", Assert.Throws<NativeAutomationException>(() => monitor.Validate(first)).Code);
        monitor.Validate(other);
        var replacement = monitor.Capture(42, 100);
        Assert.NotEqual(first, replacement);
        monitor.Validate(replacement);
        events.Destroy(42);
        Assert.Throws<NativeAutomationException>(() => monitor.Validate(first));
        Assert.Throws<NativeAutomationException>(() => monitor.Validate(replacement));
    }

    [Fact]
    public void Delayed_destroy_conservatively_invalidates_a_new_binding_without_reusing_its_stamp()
    {
        var events = new TestEvents();
        using var monitor = new NativeWindowLifetimeMonitor(events);
        events.Destroy(42); // No stamp exists yet; a previously destroyed window is ignored.
        var replacement = monitor.Capture(42, 100);
        monitor.Validate(replacement);
        events.Destroy(42); // A late event cannot be attributed to a particular HWND generation.
        Assert.Equal("stale_node", Assert.Throws<NativeAutomationException>(() => monitor.Validate(replacement)).Code);
        var retry = monitor.Capture(42, 100);
        Assert.NotEqual(replacement, retry);
        monitor.Validate(retry);
    }

    [Fact]
    public void Each_owned_process_is_subscribed_before_capture_and_process_changes_invalidate_the_old_stamp()
    {
        var events = new TestEvents();
        using var monitor = new NativeWindowLifetimeMonitor(events);
        var original = monitor.Capture(42, 100);
        var owned = monitor.Capture(43, 200);
        var reused = monitor.Capture(42, 200);
        Assert.Equal([100, 200, 200], events.ProcessRequests);
        Assert.Throws<NativeAutomationException>(() => monitor.Validate(original));
        monitor.Validate(owned);
        monitor.Validate(reused);
    }

    [Fact]
    public void Hook_failure_rejects_existing_stamps_and_future_captures()
    {
        var events = new TestEvents();
        using var monitor = new NativeWindowLifetimeMonitor(events);
        var original = monitor.Capture(42, 100);
        events.InstallFailure = new InvalidOperationException("Hook installation failed.");
        Assert.Equal("lifetime_monitor_unavailable", Assert.Throws<NativeAutomationException>(() => monitor.Capture(43, 200)).Code);
        Assert.Equal("lifetime_monitor_unavailable", Assert.Throws<NativeAutomationException>(() => monitor.Validate(original)).Code);
        Assert.Equal("lifetime_monitor_unavailable", Assert.Throws<NativeAutomationException>(() => monitor.Capture(42, 100)).Code);
    }

    [Fact]
    public void Pump_failure_and_disposal_reject_stamps_without_falling_back_to_unmonitored_ids()
    {
        var events = new TestEvents();
        using var monitor = new NativeWindowLifetimeMonitor(events);
        var original = monitor.Capture(42, 100);
        events.Fail(new InvalidOperationException("Message pump stopped."));
        Assert.Equal("lifetime_monitor_unavailable", Assert.Throws<NativeAutomationException>(() => monitor.Validate(original)).Code);
        monitor.Dispose();
        Assert.True(events.Disposed);
        Assert.Equal("lifetime_monitor_unavailable", Assert.Throws<NativeAutomationException>(() => monitor.Capture(42, 100)).Code);
    }

    private sealed class TestEvents : INativeWindowLifetimeEventSource
    {
        private Action<nint>? _destroyed;
        private Action<Exception>? _failed;
        internal List<int> ProcessRequests { get; } = [];
        internal Exception? InstallFailure { get; set; }
        internal bool Disposed { get; private set; }
        public void Start(Action<nint> destroyed, Action<Exception> failed) { _destroyed = destroyed; _failed = failed; }
        public void EnsureProcess(int processId)
        {
            ProcessRequests.Add(processId);
            if (InstallFailure is not null) throw InstallFailure;
        }
        internal void Destroy(nint handle) => _destroyed!(handle);
        internal void Fail(Exception exception) => _failed!(exception);
        public void Dispose() => Disposed = true;
    }
}
