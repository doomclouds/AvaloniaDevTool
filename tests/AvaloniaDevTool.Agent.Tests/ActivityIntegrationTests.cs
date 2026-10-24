using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class ActivityIntegrationTests
{
    [AvaloniaFact]
    public void Log_capture_preserves_existing_sink_and_applies_capture_level()
    {
        var original = Logger.Sink;
        var existing = new RecordingSink();
        Logger.Sink = existing;
        try
        {
            using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
            service.Start(new ActivityStartRequest(MinimumLogLevel: "Warning"));
            Logger.Sink!.Log(LogEventLevel.Information, "Activity.Test", this, "Information {Count}", 1);
            Logger.Sink!.Log(LogEventLevel.Warning, "Activity.Test", this, "Retry {Attempt} at {Duration:0.0} {{literal}}", 3, 1.26);
            var entries = service.Snapshot().Where(entry => entry.Category == "Activity.Test").ToArray();
            Assert.Equal(2, existing.Messages.Count);
            var entry = Assert.Single(entries);
            Assert.Equal("Warning", entry.Level);
            Assert.Equal("Retry 3 at 1.3 {literal}", entry.Message);
            Assert.Equal("Log", entry.Kind);
            Assert.NotNull(entry.ObjectId);
            service.Stop();
            Assert.Same(existing, Logger.Sink);
        }
        finally { Logger.Sink = original; }
    }

    [AvaloniaFact]
    public void Stopping_capture_does_not_replace_a_new_application_sink()
    {
        var original = Logger.Sink;
        var existing = new RecordingSink();
        var replacement = new RecordingSink();
        Logger.Sink = existing;
        try
        {
            using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
            service.Start(new ActivityStartRequest());
            var captureSink = Logger.Sink!;
            Logger.Sink = replacement;
            service.Stop();
            Assert.Same(replacement, Logger.Sink);
            captureSink.Log(LogEventLevel.Warning, "Activity.Test", this, "Stale wrapper");
            Assert.Empty(service.Snapshot());
            Assert.Single(existing.Messages);
        }
        finally { Logger.Sink = original; }
    }

    [AvaloniaFact]
    public async Task Background_log_flood_is_bounded_and_notifications_run_on_ui_thread()
    {
        var original = Logger.Sink;
        Logger.Sink = null;
        try
        {
            var observedOffThread = false;
            using var service = new ActivityService(new WeakObjectRegistry(), (method, _) =>
            {
                if (method == ActivityMethods.EntryAdded && !Dispatcher.UIThread.CheckAccess())
                    observedOffThread = true;
            });
            service.Start(new ActivityStartRequest());
            var captureSink = Logger.Sink!;
            await Task.Run(() =>
            {
                for (var index = 0; index < 1200; index++)
                    captureSink.Log(LogEventLevel.Warning, "Activity.Test", null, "Entry {Index}", index);
            });
            var entries = service.Snapshot();
            Assert.Equal(500, entries.Count);
            Assert.Equal("Entry 1199", entries[^1].Message);
            Assert.True(service.Status().DroppedCount >= 700);
            Assert.Equal(500, service.Status().BufferedCount);
            Assert.False(observedOffThread);
        }
        finally { Logger.Sink = original; }
    }

    [AvaloniaFact]
    public void Clear_keeps_capture_running_and_resets_buffer_and_loss_count()
    {
        var original = Logger.Sink;
        Logger.Sink = null;
        try
        {
            using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
            service.Start(new ActivityStartRequest());
            Logger.Sink!.Log(LogEventLevel.Warning, "Activity.Test", null, "Before");
            var first = Assert.Single(service.Snapshot());
            service.Clear();
            Assert.Empty(service.Snapshot());
            Assert.True(service.Status().IsRunning);
            Assert.Equal(0, service.Status().DroppedCount);
            Logger.Sink!.Log(LogEventLevel.Warning, "Activity.Test", null, "After");
            var second = Assert.Single(service.Snapshot());
            Assert.True(second.Sequence > first.Sequence);
            Assert.Equal("After", second.Message);
            service.Stop();
            Assert.False(service.Status().IsRunning);
        }
        finally { Logger.Sink = original; }
    }

    [AvaloniaFact]
    public void Routed_events_include_route_nodes_final_handled_state_and_source_filter()
    {
        var source = new ProbeControl { Name = "Source" };
        var other = new ProbeControl { Name = "Other" };
        var panel = new StackPanel { Children = { source, other } };
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            var registry = new WeakObjectRegistry();
            var sourceId = registry.GetId(source);
            using var service = new ActivityService(registry, (_, _) => { });
            service.Start(new ActivityStartRequest(CaptureLogs: false, CaptureEvents: true,
                ObjectId: sourceId, EventName: "ActivityProbe"));
            source.AddHandler(ProbeControl.ProbeEvent, (_, args) => args.Handled = true, RoutingStrategies.Bubble);
            other.RaiseEvent(new RoutedEventArgs(ProbeControl.ProbeEvent));
            Assert.Empty(service.Snapshot());
            source.RaiseEvent(new RoutedEventArgs(ProbeControl.ProbeEvent));
            var entries = service.Snapshot();
            Assert.NotEmpty(entries);
            Assert.All(entries, entry => Assert.Equal(sourceId, entry.ObjectId));
            Assert.Contains(entries, entry => entry.Route == "Tunnel");
            Assert.Contains(entries, entry => entry.Route == "Bubble");
            Assert.Contains(entries, entry => entry.HandlerObjectId is { } id && id != sourceId);
            Assert.Contains(entries, entry => entry.Route == "Finished" && entry.Handled == true);
            var beforeStop = entries.Count;
            service.Stop();
            source.RaiseEvent(new RoutedEventArgs(ProbeControl.ProbeEvent));
            Assert.Equal(beforeStop, service.Snapshot().Count);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Event_name_filter_does_not_capture_unrelated_events()
    {
        var source = new ProbeControl();
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
        service.Start(new ActivityStartRequest(CaptureLogs: false, CaptureEvents: true, EventName: "UnmatchedEvent"));
        source.RaiseEvent(new RoutedEventArgs(ProbeControl.ProbeEvent));
        Assert.Empty(service.Snapshot());
        service.Start(new ActivityStartRequest(CaptureLogs: false, CaptureEvents: true, EventName: "*"));
        source.RaiseEvent(new RoutedEventArgs(ProbeControl.ProbeEvent));
        Assert.Contains(service.Snapshot(), entry => entry.EventName == "ActivityProbe");
    }

    [AvaloniaFact]
    public void Invalid_settings_leave_existing_capture_running()
    {
        var original = Logger.Sink;
        Logger.Sink = null;
        try
        {
            using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
            service.Start(new ActivityStartRequest());
            var sink = Logger.Sink;
            var error = Assert.Throws<RpcException>(() =>
                service.Start(new ActivityStartRequest(MinimumLogLevel: "NotALevel")));
            Assert.Equal("invalid_request", error.Code);
            Assert.Same(sink, Logger.Sink);
            Assert.True(service.Status().IsRunning);
            error = Assert.Throws<RpcException>(() =>
                service.Start(new ActivityStartRequest(CaptureLogs: false, CaptureEvents: false)));
            Assert.Equal("invalid_request", error.Code);
            error = Assert.Throws<RpcException>(() =>
                service.Start(new ActivityStartRequest(ObjectId: 9999)));
            Assert.Equal("object_expired", error.Code);
            Assert.Same(sink, Logger.Sink);
        }
        finally { Logger.Sink = original; }
    }

    [AvaloniaFact]
    public void Pending_log_entries_do_not_keep_source_objects_alive()
    {
        var original = Logger.Sink;
        Logger.Sink = null;
        try
        {
            using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
            service.Start(new ActivityStartRequest());
            var reference = LogTransientSource(Logger.Sink!);
            for (var index = 0; index < 3; index++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            Assert.False(reference.IsAlive);
            Assert.Null(Assert.Single(service.Snapshot()).ObjectId);
        }
        finally { Logger.Sink = original; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LogTransientSource(ILogSink sink)
    {
        var source = new object();
        sink.Log(LogEventLevel.Warning, "Activity.Test", source, "Transient");
        return new WeakReference(source);
    }

    private sealed class ProbeControl : Control
    {
        public static readonly RoutedEvent<RoutedEventArgs> ProbeEvent =
            RoutedEvent.Register<ProbeControl, RoutedEventArgs>("ActivityProbe", RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        static ProbeControl() { }
    }

    private sealed class RecordingSink : ILogSink
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public bool IsEnabled(LogEventLevel level, string area) => true;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Messages.Enqueue(messageTemplate);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) => Messages.Enqueue(messageTemplate);
    }
}

