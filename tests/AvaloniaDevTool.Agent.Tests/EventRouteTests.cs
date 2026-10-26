using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class EventRouteTests
{
    [AvaloniaFact]
    public void Real_tunnel_and_bubble_share_correlation_and_observe_handled_without_attributing_a_handler()
    {
        var child = new EventProbe { Name = "Child" };
        var parent = new StackPanel { Name = "Parent", Children = { child } };
        var window = new Window { Content = parent };
        window.Show();
        try
        {
            var registry = new WeakObjectRegistry();
            using var service = new ActivityService(registry, (_, _) => { });
            var catalog = service.GetEventCatalog();
            var item = Assert.Single(catalog.Events, item => item.Name == "RouteProbe");
            Assert.Equal("Tunnel, Bubble", item.RoutingStrategies);
            service.Start(new(false, true, EventSelection: new([item.Id])));
            var skipped = 0;
            var handledToo = 0;
            child.AddHandler(EventProbe.Routed, (_, args) => args.Handled = true, RoutingStrategies.Bubble);
            parent.AddHandler(EventProbe.Routed, (_, _) => skipped++, RoutingStrategies.Bubble);
            parent.AddHandler(EventProbe.Routed, (_, _) => handledToo++, RoutingStrategies.Bubble, true);
            child.RaiseEvent(new RoutedEventArgs(EventProbe.Routed));
            var route = Assert.Single(service.EventRoutesSnapshot());
            Assert.True(route.IsFinished);
            Assert.True(route.FinalHandled);
            Assert.Equal("Finished", route.State);
            Assert.Equal(["Tunnel", "Bubble"], route.FinishedStrategies);
            var tunnel = route.Nodes.Where(item => item.Strategy == "Tunnel").ToArray();
            var bubble = route.Nodes.Where(item => item.Strategy == "Bubble").ToArray();
            Assert.Equal(tunnel.Select(item => item.ReceiverObjectId).Reverse(),
                bubble.Select(item => item.ReceiverObjectId));
            Assert.Contains(route.Nodes, node => node.ReceiverType == typeof(Window).FullName);
            Assert.Equal(registry.GetId(child), tunnel[^1].ReceiverObjectId);
            Assert.False(bubble[0].HandledObserved);
            Assert.True(bubble[1].HandledObserved);
            Assert.True(bubble[1].HandledChangedSincePreviousObservation);
            Assert.All(route.Nodes, item => Assert.Equal("RaisedClassHandler", item.Observation));
            Assert.Equal(0, skipped);
            Assert.Equal(1, handledToo);
            Assert.All(service.Snapshot(), item => Assert.Equal(route.RouteId, item.EventRouteId));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Direct_events_reused_args_and_child_reentrancy_have_distinct_finished_correlations()
    {
        var child = new EventProbe();
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
        service.Start(new(false, true, EventName: "*"));
        child.AddHandler(EventProbe.Routed,
            (_, args) =>
            {
                if (args.Route == RoutingStrategies.Bubble)
                    child.RaiseEvent(new RoutedEventArgs(EventProbe.Direct));
            },
            RoutingStrategies.Bubble);
        var reused = new RoutedEventArgs(EventProbe.Routed);
        child.RaiseEvent(reused);
        child.RaiseEvent(reused);
        var routes = service.EventRoutesSnapshot();
        Assert.Equal(4, routes.Count);
        Assert.Equal(4, routes.Select(item => item.RouteId).Distinct().Count());
        Assert.All(routes, item => Assert.True(item.IsFinished));
        Assert.All(routes.Where(item => item.EventName == "DirectProbe"),
            item =>
            {
                Assert.Equal("Direct", Assert.Single(item.Nodes).Strategy);
                Assert.Equal(["Direct"], item.FinishedStrategies);
            });
    }

    [AvaloniaFact]
    public void Exact_catalog_selection_and_late_registration_are_applied_to_real_events()
    {
        var child = new EventProbe();
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
        var item = service.GetEventCatalog().Events.Single(item => item.Name == "DirectProbe");
        service.Start(new(false, true, EventSelection: new([item.Id])));
        child.RaiseEvent(new RoutedEventArgs(EventProbe.Routed));
        Assert.Empty(service.EventRoutesSnapshot());
        child.RaiseEvent(new RoutedEventArgs(EventProbe.Direct));
        Assert.Equal("DirectProbe", Assert.Single(service.EventRoutesSnapshot()).EventName);
        service.Clear();
        service.Start(new(false, true, EventSelection: new(Groups: ["Control"])));
        var late = RoutedEvent.Register<EventProbe, RoutedEventArgs>("LateRoute" + Guid.NewGuid().ToString("N"),
            RoutingStrategies.Direct);
        var metadata = Assert.Single(service.GetEventCatalog().Events, item => item.Name == late.Name);
        Assert.Equal("Control", metadata.Group);
        child.RaiseEvent(new RoutedEventArgs(late));
        Assert.Equal(metadata.Id, Assert.Single(service.EventRoutesSnapshot()).EventId);
        Assert.Equal(6, service.GetEventCatalog().DefaultGroups.Count);
    }

    [AvaloniaFact]
    public void Recursively_raising_same_direct_args_keeps_inner_and_outer_correlations_separate()
    {
        var child = new EventProbe();
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
        service.Start(new(false, true, EventName: "DirectProbe"));
        var nested = false;
        child.AddHandler(EventProbe.Direct,
            (_, args) =>
            {
                if (nested)
                    return;
                nested = true;
                child.RaiseEvent(args);
            });
        child.RaiseEvent(new RoutedEventArgs(EventProbe.Direct));
        var routes = service.EventRoutesSnapshot();
        Assert.Equal(2, routes.Count);
        Assert.All(routes,
            route =>
            {
                Assert.True(route.IsFinished);
                Assert.Single(route.Nodes);
            });
        Assert.NotEqual(routes[0].RouteId, routes[1].RouteId);
    }

    [AvaloniaFact]
    public void Clear_inside_instance_handler_cannot_resurrect_old_route_and_stop_keeps_history()
    {
        var child = new EventProbe();
        var parent = new StackPanel { Children = { child } };
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
        service.Start(new(false, true, EventName: "RouteProbe"));
        var clear = true;
        child.AddHandler(EventProbe.Routed,
            (_, _) =>
            {
                if (clear)
                {
                    clear = false;
                    service.Clear();
                }
            },
            RoutingStrategies.Bubble);
        child.RaiseEvent(new RoutedEventArgs(EventProbe.Routed));
        Assert.Empty(service.EventRoutesSnapshot());
        child.RaiseEvent(new RoutedEventArgs(EventProbe.Routed));
        var route = Assert.Single(service.EventRoutesSnapshot());
        Assert.True(route.IsFinished);
        service.Stop();
        child.RaiseEvent(new RoutedEventArgs(EventProbe.Routed));
        Assert.Equal(route.RouteId, Assert.Single(service.EventRoutesSnapshot()).RouteId);
        GC.KeepAlive(parent);
    }

    [AvaloniaFact]
    public void Route_history_has_count_byte_and_per_route_limits()
    {
        var child = new EventProbe();
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
        service.Start(new(false, true, EventName: "RouteProbe"));
        for (var index = 0; index < 550; index++)
            child.RaiseEvent(new RoutedEventArgs(EventProbe.Routed));
        var status = service.EventRoutesStatus();
        Assert.Equal(ActivityService.EventRouteCapacity, status.BufferedCount);
        Assert.True(status.BufferedBytes <= status.ByteBudget);
        Assert.Equal(350, status.DroppedCount);
        service.Clear();
        Control root = child;
        for (var index = 0; index < 150; index++)
            root = new Border { Child = root };
        child.RaiseEvent(new RoutedEventArgs(EventProbe.Routed));
        var route = Assert.Single(service.EventRoutesSnapshot());
        Assert.Equal(ActivityService.MaximumRouteNodes, route.Nodes.Count);
        Assert.True(route.IsTruncated);
        Assert.True(route.IsFinished);
        GC.KeepAlive(root);
    }

    [AvaloniaFact]
    public void Route_history_and_correlations_do_not_retain_source_receiver_or_event_args()
    {
        _ = EventProbe.Routed;
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
        service.Start(new(false, true, EventName: "RouteProbe"));
        var references = RaiseTransient(service);
        for (var index = 0; index < 3; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.All(references, reference => Assert.False(reference.IsAlive));
        var route = Assert.Single(service.EventRoutesSnapshot());
        Assert.True(route.SourceExpired);
        Assert.All(route.Nodes, node => Assert.True(node.ReceiverExpired));
    }

    [AvaloniaFact]
    public void Unfinished_routes_expire_without_inventing_completion_and_history_expires_after_ttl()
    {
        var child = new EventProbe();
        var clock = new ManualTimeProvider();
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { }, timeProvider: clock);
        service.Start(new(false, true, EventName: "DirectProbe"));
        child.AddHandler(EventProbe.Direct,
            (_, _) => throw new InvalidOperationException("Application handler failed"));
        Assert.Throws<InvalidOperationException>(() => child.RaiseEvent(new RoutedEventArgs(EventProbe.Direct)));
        Assert.Equal("Active", Assert.Single(service.EventRoutesSnapshot()).State);
        clock.Advance(TimeSpan.FromSeconds(ActivityService.ActiveRouteTtlSeconds + 1));
        var route = Assert.Single(service.EventRoutesSnapshot());
        Assert.Equal("Expired", route.State);
        Assert.False(route.IsFinished);
        Assert.True(route.IsTruncated);
        Assert.Null(route.FinalHandled);
        Assert.Equal(1, service.EventRoutesStatus().TruncatedCount);
        clock.Advance(TimeSpan.FromSeconds(ActivityService.EventRouteHistoryTtlSeconds));
        Assert.Empty(service.EventRoutesSnapshot());
        Assert.Equal(0, service.EventRoutesStatus().BufferedBytes);
    }

    [AvaloniaFact]
    public void Large_real_routes_are_evicted_by_utf8_json_budget_before_count_capacity()
    {
        var source = new EventProbe { Name = new string('界', 256) };
        Control root = source;
        for (var index = 0; index < 70; index++)
            root = new Border { Name = new string('界', 256), Child = root };
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
        service.Start(new(false, true, EventName: "RouteProbe"));
        for (var index = 0; index < 30; index++)
            source.RaiseEvent(new RoutedEventArgs(EventProbe.Routed));
        var status = service.EventRoutesStatus();
        Assert.True(status.DroppedCount > 0);
        Assert.True(status.BufferedCount < status.Capacity);
        Assert.InRange(status.BufferedBytes, 1, status.ByteBudget);
        var snapshot = service.EventRoutesSnapshot();
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(snapshot, ProtocolJson.Options).Length <=
                    ActivityService.EventRouteByteBudget);
        GC.KeepAlive(root);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] RaiseTransient(ActivityService service)
    {
        var source = new EventProbe();
        var parent = new StackPanel { Children = { source } };
        var args = new RoutedEventArgs(EventProbe.Routed);
        source.RaiseEvent(args);
        Assert.Single(service.EventRoutesSnapshot());
        return [new(source), new(parent), new(args)];
    }

    private sealed class EventProbe : Control
    {
        public static readonly RoutedEvent<RoutedEventArgs> Routed =
            RoutedEvent.Register<EventProbe, RoutedEventArgs>("RouteProbe",
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble);

        public static readonly RoutedEvent<RoutedEventArgs> Direct =
            RoutedEvent.Register<EventProbe, RoutedEventArgs>("DirectProbe", RoutingStrategies.Direct);

        static EventProbe()
        {
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan interval) => _now += interval;
    }
}
