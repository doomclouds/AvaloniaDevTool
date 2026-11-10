using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Logging;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class BreakpointIntegrationTests
{
    [AvaloniaFact]
    public void Property_breakpoints_apply_all_hit_count_comparisons_and_keep_summary()
    {
        var cases = new (BreakpointComparison Comparison, bool[] Expected)[]
        {
            (BreakpointComparison.Always, [true, true, true]),
            (BreakpointComparison.LessThan, [true, false, false]),
            (BreakpointComparison.LessThanOrEqual, [true, true, false]),
            (BreakpointComparison.Equal, [false, true, false]),
            (BreakpointComparison.GreaterThan, [false, false, true]),
            (BreakpointComparison.GreaterThanOrEqual, [false, true, true])
        };
        foreach (var (comparison, expected) in cases)
        {
            var text = new TextBlock();
            var objects = new WeakObjectRegistry();
            var id = objects.GetId(text);
            using var service = new BreakpointService(objects, (_, _) => { });
            var breakpoint = service.Create(new CreateBreakpointRequest(
                PropertySpec(id) with { Comparison = comparison, CompareTo = 2 }));
            text.Text = "one";
            text.Text = "two";
            text.Text = "three";
            Assert.Equal(expected, service.History().Hits.Select(hit => hit.ConditionMet));
            Assert.Equal(3, Assert.Single(service.List()).HitCount);
            var latest = service.History().Hits[^1];
            Assert.Equal(breakpoint.Id, latest.BreakpointId);
            Assert.Equal(id, latest.ObjectId);
            Assert.Contains("three", latest.Summary);
            Assert.InRange(latest.Timestamp, DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow);
        }
    }

    [AvaloniaFact]
    public void Readonly_properties_can_be_observed_with_complete_owner_id()
    {
        var border = new Border();
        var objects = new WeakObjectRegistry();
        var id = objects.GetId(border);
        using var service = new BreakpointService(objects, (_, _) => { });
        service.Create(new CreateBreakpointRequest(new BreakpointSpec(BreakpointKind.Property,
            PropertyId(Control.BoundsProperty),
            id,
            LogMessage: false)));
        border.Measure(new Size(140, 40));
        border.Arrange(new Rect(0, 0, 140, 40));
        Assert.NotEmpty(service.History().Hits);
        var bad = Assert.Throws<RpcException>(() => service.Create(new CreateBreakpointRequest(
            new BreakpointSpec(BreakpointKind.Property, "Bounds", id, LogMessage: false))));
        Assert.Equal("invalid_member", bad.Code);
    }

    [AvaloniaFact]
    public void Update_can_enable_disable_reset_and_replace_member_without_stale_listener()
    {
        var text = new TextBlock();
        var objects = new WeakObjectRegistry();
        var id = objects.GetId(text);
        using var service = new BreakpointService(objects, (_, _) => { });
        var definition = PropertySpec(id) with { Enabled = false };
        var breakpoint = service.Create(new CreateBreakpointRequest(definition));
        text.Text = "disabled";
        Assert.Empty(service.History().Hits);
        service.Update(new UpdateBreakpointRequest(breakpoint.Id, definition with { Enabled = true }));
        text.Text = "first";
        Assert.Equal(1, Assert.Single(service.List()).HitCount);
        service.Update(new UpdateBreakpointRequest(breakpoint.Id, definition));
        text.Text = "ignored";
        Assert.Single(service.History().Hits);
        var width = definition with { Enabled = true, MemberId = PropertyId(Control.WidthProperty) };
        service.Update(new UpdateBreakpointRequest(breakpoint.Id, width, ResetHitCount: true));
        text.Text = "old member";
        Assert.Single(service.History().Hits);
        text.Width = 42;
        Assert.Equal(1, Assert.Single(service.List()).HitCount);
        Assert.Equal(2, service.History().Hits.Count);
        service.Remove(new RemoveBreakpointRequest(breakpoint.Id));
        text.Width = 43;
        Assert.Empty(service.List());
        Assert.Equal(2, service.History().Hits.Count);
    }

    [AvaloniaFact]
    public void Failed_update_preserves_previous_breakpoint_and_observation()
    {
        var text = new TextBlock();
        var objects = new WeakObjectRegistry();
        var id = objects.GetId(text);
        using var service = new BreakpointService(objects, (_, _) => { });
        var definition = PropertySpec(id);
        var breakpoint = service.Create(new CreateBreakpointRequest(definition));
        var error = Assert.Throws<RpcException>(() => service.Update(new UpdateBreakpointRequest(
            breakpoint.Id,
            definition with { MemberId = "Missing.Type:Unknown" })));
        Assert.Equal("property_not_found", error.Code);
        text.Text = "still observed";
        Assert.Equal(1, Assert.Single(service.List()).HitCount);
    }

    [AvaloniaFact]
    public async Task Routed_event_is_counted_once_after_tunnel_and_bubble_complete()
    {
        var probe = new RouteProbe();
        var panel = new StackPanel { Children = { probe } };
        var window = new Window { Content = panel };
        window.Show();
        try
        {
            var objects = new WeakObjectRegistry();
            var panelId = objects.GetId(panel);
            var sourceId = objects.GetId(probe);
            var notifications = new List<BreakpointHit>();
            using var service = new BreakpointService(objects,
                (method, payload) =>
                {
                    if (method == BreakpointMethods.Hit)
                        notifications.Add((BreakpointHit)payload);
                });
            var member = RouteProbe.ProbeEvent.OwnerType.FullName + ":" + RouteProbe.ProbeEvent.Name;
            service.Create(new CreateBreakpointRequest(new BreakpointSpec(BreakpointKind.Event,
                member,
                LogMessage: false)));
            service.Create(new CreateBreakpointRequest(new BreakpointSpec(BreakpointKind.Event,
                member,
                panelId,
                LogMessage: false)));
            panel.AddHandler(RouteProbe.ProbeEvent,
                (_, args) => args.Handled = true,
                RoutingStrategies.Tunnel,
                true);
            probe.RaiseEvent(new RoutedEventArgs(RouteProbe.ProbeEvent) { Source = probe });
            await WaitUntilAsync(() => service.History().Hits.Count == 2 && notifications.Count == 2);
            Assert.All(service.List(), info => Assert.Equal(1, info.HitCount));
            Assert.All(service.History().Hits,
                hit =>
                {
                    Assert.Equal(sourceId, hit.ObjectId);
                    Assert.Contains("handled=True", hit.Summary);
                });
            probe.RaiseEvent(new RoutedEventArgs(RouteProbe.ProbeEvent) { Source = probe });
            await WaitUntilAsync(() => service.History().Hits.Count == 4);
            Assert.All(service.List(), info => Assert.Equal(2, info.HitCount));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void One_shot_removes_listener_only_after_comparison_matches()
    {
        var text = new TextBlock();
        var objects = new WeakObjectRegistry();
        using var service = new BreakpointService(objects, (_, _) => { });
        service.Create(new CreateBreakpointRequest(PropertySpec(objects.GetId(text)) with
        {
            Comparison = BreakpointComparison.Equal, CompareTo = 2, RemoveOnHit = true
        }));
        text.Text = "first";
        Assert.Single(service.List());
        text.Text = "second";
        Assert.Empty(service.List());
        text.Text = "third";
        Assert.Equal(2, service.History().Hits.Count);
        Assert.False(service.History().Hits[0].ConditionMet);
        Assert.True(service.History().Hits[1].ConditionMet);
    }

    [AvaloniaFact]
    public void Suspension_requires_attached_debugger_and_disabled_configuration_cannot_be_enabled_without_one()
    {
        if (Debugger.IsAttached)
            return;
        var text = new TextBlock();
        var objects = new WeakObjectRegistry();
        using var service = new BreakpointService(objects, (_, _) => { });
        var definition = PropertySpec(objects.GetId(text));
        var error = Assert.Throws<RpcException>(() => service.Create(new CreateBreakpointRequest(
            definition with { Suspend = true })));
        Assert.Equal("debugger_not_attached", error.Code);
        Assert.Empty(service.List());
        var regular = service.Create(new CreateBreakpointRequest(definition));
        service.Update(new UpdateBreakpointRequest(regular.Id, definition with { Enabled = false, Suspend = true }));
        error = Assert.Throws<RpcException>(() => service.Update(new UpdateBreakpointRequest(
            regular.Id,
            definition with { Enabled = true, Suspend = true })));
        Assert.Equal("debugger_not_attached", error.Code);
        Assert.Equal(BreakpointState.Disabled, Assert.Single(service.List()).State);
    }

    [AvaloniaFact]
    public void History_is_bounded_and_reports_discarded_retention()
    {
        var text = new TextBlock();
        var objects = new WeakObjectRegistry();
        using var service = new BreakpointService(objects, (_, _) => { });
        service.Create(new CreateBreakpointRequest(PropertySpec(objects.GetId(text))));
        for (var index = 0; index < 250; index++)
            text.Text = "value " + index;
        var history = service.History();
        Assert.Equal(200, history.Hits.Count);
        Assert.Equal(50, history.DroppedCount);
        Assert.Equal(51, history.Hits[0].HitCount);
        Assert.Equal(250, Assert.Single(service.List()).HitCount);
    }

    [AvaloniaFact]
    public void Observing_target_and_recording_hits_do_not_prevent_target_collection()
    {
        var objects = new WeakObjectRegistry();
        using var service = new BreakpointService(objects, (_, _) => { });
        var targetId = RegisterTransientTarget(objects, service);
        Collect();
        Assert.False(objects.TryGet(targetId, out _));
        Assert.Equal(BreakpointState.ObjectExpired, Assert.Single(service.List()).State);
        var error = Assert.Throws<RpcException>(() => service.Update(new UpdateBreakpointRequest(
            service.List()[0].Id,
            PropertySpec(targetId))));
        Assert.Equal("object_expired", error.Code);
    }

    [AvaloniaFact]
    public void Dispose_releases_global_subscription_and_service_can_be_collected()
    {
        var objects = new WeakObjectRegistry();
        var weakService = CreateDisposedGlobalService(objects);
        Collect();
        Assert.False(weakService.TryGetTarget(out _));
    }

    [AvaloniaFact]
    public void Dispose_releases_global_routed_event_subscription()
    {
        var objects = new WeakObjectRegistry();
        var weakService = CreateDisposedGlobalEventService(objects);
        Collect();
        Assert.False(weakService.TryGetTarget(out _));
    }

    [AvaloniaFact]
    public void Logging_observer_reentrancy_does_not_create_breakpoint_feedback_loop()
    {
        var text = new TextBlock();
        var objects = new WeakObjectRegistry();
        using var service = new BreakpointService(objects, (_, _) => { });
        service.Create(new CreateBreakpointRequest(PropertySpec(objects.GetId(text)) with { LogMessage = true }));
        var previous = Logger.Sink;
        var sink = new ReentrantSink(() => text.Text = "changed by log observer");
        Logger.Sink = sink;
        try
        {
            text.Text = "first";
            Assert.Equal(1, sink.Count);
            Assert.Equal(1, Assert.Single(service.List()).HitCount);
        }
        finally
        {
            Logger.Sink = previous;
        }
    }

    private static BreakpointSpec PropertySpec(long objectId) =>
        new(BreakpointKind.Property, PropertyId(TextBlock.TextProperty), objectId, LogMessage: false);

    private static string PropertyId(AvaloniaProperty property) => property.OwnerType.FullName + ":" + property.Name;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RegisterTransientTarget(WeakObjectRegistry objects, BreakpointService service)
    {
        var target = new TextBlock();
        var id = objects.GetId(target);
        service.Create(new CreateBreakpointRequest(PropertySpec(id)));
        target.Text = "recorded without retaining target";
        return id;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<BreakpointService> CreateDisposedGlobalService(WeakObjectRegistry objects)
    {
        var service = new BreakpointService(objects, (_, _) => { });
        service.Create(new CreateBreakpointRequest(new BreakpointSpec(BreakpointKind.Property,
            PropertyId(TextBlock.TextProperty),
            LogMessage: false)));
        service.Dispose();
        return new WeakReference<BreakpointService>(service);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<BreakpointService> CreateDisposedGlobalEventService(WeakObjectRegistry objects)
    {
        var service = new BreakpointService(objects, (_, _) => { });
        service.Create(new CreateBreakpointRequest(new BreakpointSpec(BreakpointKind.Event,
            RouteProbe.ProbeEvent.OwnerType.FullName + ":" + RouteProbe.ProbeEvent.Name,
            LogMessage: false)));
        service.Dispose();
        return new WeakReference<BreakpointService>(service);
    }

    private static void Collect()
    {
        for (var index = 0; index < 3; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(15, timeout.Token);
    }

    private sealed class RouteProbe : Control
    {
        public static readonly RoutedEvent<RoutedEventArgs> ProbeEvent =
            RoutedEvent.Register<RouteProbe, RoutedEventArgs>("Probe",
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    private sealed class ReentrantSink(Action callback) : ILogSink
    {
        public int Count { get; private set; }
        public bool IsEnabled(LogEventLevel level, string area) => area == "DevTool.Breakpoints";

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            Count++;
            callback();
        }

        public void Log(LogEventLevel level,
            string area,
            object? source,
            string messageTemplate,
            params object?[] propertyValues) =>
            Log(level, area, source, messageTemplate);
    }
}
