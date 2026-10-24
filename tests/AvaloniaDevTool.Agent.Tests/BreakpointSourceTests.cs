using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class BreakpointSourceTests
{
    [AvaloniaFact]
    public void Source_and_receiver_are_independent_filters_and_legacy_object_id_keeps_receiver_semantics()
    {
        var first = new Probe();
        var second = new Probe();
        var parent = new StackPanel { Children = { first, second } };
        var outside = new Probe();
        var window = new Window { Content = new StackPanel { Children = { parent, outside } } };
        window.Show();
        try
        {
            var objects = new WeakObjectRegistry();
            using var service = new BreakpointService(objects, (_, _) => { });
            var firstId = objects.GetId(first);
            var parentId = objects.GetId(parent);
            var global = Create(service, EventSpec());
            var legacyReceiver = Create(service, EventSpec() with { ObjectId = parentId });
            var onlySource = Create(service, EventSpec() with { SourceObjectId = firstId });
            var both = Create(service, EventSpec() with { ObjectId = parentId, SourceObjectId = firstId });
            var wrongReceiver = Create(service, EventSpec() with { ObjectId = objects.GetId(outside), SourceObjectId = firstId });
            parent.AddHandler(Probe.Routed, (_, args) => args.Handled = true, RoutingStrategies.Tunnel, true);
            first.RaiseEvent(new RoutedEventArgs(Probe.Routed) { Source = second });
            second.RaiseEvent(new RoutedEventArgs(Probe.Routed));
            outside.RaiseEvent(new RoutedEventArgs(Probe.Routed));
            var definitions = service.List().ToDictionary(item => item.Id);
            Assert.Equal(3, definitions[global.Id].HitCount);
            Assert.Equal(2, definitions[legacyReceiver.Id].HitCount);
            Assert.Equal(1, definitions[onlySource.Id].HitCount);
            Assert.Equal(1, definitions[both.Id].HitCount);
            Assert.Equal(0, definitions[wrongReceiver.Id].HitCount);
            var sourceHit = Assert.Single(service.History().Hits, hit => hit.BreakpointId == onlySource.Id);
            Assert.Equal(firstId, sourceHit.ObjectId);
            Assert.Contains("handled=True", sourceHit.Summary);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Reusing_args_for_sequential_raises_is_counted_synchronously_once_per_raise()
    {
        var source = new Probe();
        var objects = new WeakObjectRegistry();
        using var service = new BreakpointService(objects, (_, _) => { });
        Create(service, EventSpec() with { SourceObjectId = objects.GetId(source) });
        var args = new RoutedEventArgs(Probe.Routed);
        source.RaiseEvent(args);
        source.RaiseEvent(args);
        Assert.Equal([1L, 2L], service.History().Hits.Select(hit => hit.HitCount));
        Assert.Equal(2, Assert.Single(service.List()).HitCount);
    }

    [AvaloniaFact]
    public void Nested_raises_with_distinct_args_keep_each_actual_source_and_final_handled()
    {
        var outer = new Probe();
        var inner = new Probe();
        var objects = new WeakObjectRegistry();
        using var service = new BreakpointService(objects, (_, _) => { });
        Create(service, EventSpec());
        outer.AddHandler(Probe.Routed, (_, _) => inner.RaiseEvent(new RoutedEventArgs(Probe.Routed)));
        inner.AddHandler(Probe.Routed, (_, args) => args.Handled = true);
        outer.RaiseEvent(new RoutedEventArgs(Probe.Routed));
        var hits = service.History().Hits;
        Assert.Equal(2, hits.Count);
        Assert.Equal(objects.GetId(inner), hits[0].ObjectId);
        Assert.Contains("handled=True", hits[0].Summary);
        Assert.Equal(objects.GetId(outer), hits[1].ObjectId);
        Assert.Contains("handled=False", hits[1].Summary);
    }

    [AvaloniaFact]
    public void Recursively_raising_same_direct_args_keeps_outer_source_snapshot_and_separate_count()
    {
        var outer = new Probe();
        var inner = new Probe();
        var objects = new WeakObjectRegistry();
        using var service = new BreakpointService(objects, (_, _) => { });
        var definition = EventSpec(Probe.Direct) with { SourceObjectId = objects.GetId(outer) };
        Create(service, definition);
        Create(service, EventSpec(Probe.Direct));
        var nested = false;
        outer.AddHandler(Probe.Direct, (_, args) =>
        {
            if (nested) return;
            nested = true;
            inner.RaiseEvent(args);
            args.Handled = true;
        });
        outer.RaiseEvent(new RoutedEventArgs(Probe.Direct));
        Assert.Equal([1L, 2L], service.List().Select(item => item.HitCount));
        Assert.Equal(objects.GetId(outer), service.History().Hits[^1].ObjectId);
        Assert.Contains("handled=True", service.History().Hits[^1].Summary);
    }

    [AvaloniaFact]
    public void Recursively_reusing_routed_args_counts_actual_phase_finishes_despite_inner_route_mutation()
    {
        var outer = new Probe();
        var inner = new Probe();
        var objects = new WeakObjectRegistry();
        using var service = new BreakpointService(objects, (_, _) => { });
        Create(service, EventSpec());
        Create(service, EventSpec() with { SourceObjectId = objects.GetId(outer) });
        var nested = false;
        outer.AddHandler(Probe.Routed, (_, args) =>
        {
            if (nested) return;
            nested = true;
            inner.RaiseEvent(args);
        }, RoutingStrategies.Tunnel);
        outer.RaiseEvent(new RoutedEventArgs(Probe.Routed));
        Assert.Equal([2L, 1L], service.List().Select(item => item.HitCount));
        Assert.Equal(3, service.History().Hits.Count);
        Assert.Equal(objects.GetId(inner), service.History().Hits[0].ObjectId);
        Assert.Equal(objects.GetId(outer), service.History().Hits[^1].ObjectId);
    }

    [AvaloniaFact]
    public void Source_filters_support_hit_conditions_once_disable_enable_and_replacement()
    {
        var first = new Probe();
        var second = new Probe();
        var objects = new WeakObjectRegistry();
        using var service = new BreakpointService(objects, (_, _) => { });
        var definition = EventSpec() with
        {
            SourceObjectId = objects.GetId(first), Comparison = BreakpointComparison.Equal,
            CompareTo = 2, RemoveOnHit = true, Enabled = false
        };
        var breakpoint = Create(service, definition);
        first.RaiseEvent(new RoutedEventArgs(Probe.Routed));
        Assert.Empty(service.History().Hits);
        service.Update(new(breakpoint.Id, definition with { Enabled = true }));
        first.RaiseEvent(new RoutedEventArgs(Probe.Routed));
        second.RaiseEvent(new RoutedEventArgs(Probe.Routed));
        Assert.False(Assert.Single(service.History().Hits).ConditionMet);
        var replacement = definition with { Enabled = true, SourceObjectId = objects.GetId(second) };
        service.Update(new(breakpoint.Id, replacement));
        first.RaiseEvent(new RoutedEventArgs(Probe.Routed));
        second.RaiseEvent(new RoutedEventArgs(Probe.Routed));
        second.RaiseEvent(new RoutedEventArgs(Probe.Routed));
        Assert.Empty(service.List());
        Assert.Equal(2, service.History().Hits.Count);
        Assert.True(service.History().Hits[^1].ConditionMet);
    }

    [AvaloniaFact]
    public void Collected_source_expires_a_global_receiver_breakpoint_without_retaining_source_or_history()
    {
        var objects = new WeakObjectRegistry();
        using var service = new BreakpointService(objects, (_, _) => { });
        var sourceId = CreateTransientSource(objects, service);
        for (var index = 0; index < 3; index++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Assert.False(objects.TryGet(sourceId, out _));
        Assert.Equal(BreakpointState.ObjectExpired, Assert.Single(service.List()).State);
        var error = Assert.Throws<RpcException>(() => service.Update(new(service.List()[0].Id,
            EventSpec() with { SourceObjectId = sourceId })));
        Assert.Equal("object_expired", error.Code);
    }

    [AvaloniaFact]
    public void Invalid_source_definition_is_rejected_and_failed_update_keeps_old_receiver_observation()
    {
        var source = new Probe();
        var objects = new WeakObjectRegistry();
        using var service = new BreakpointService(objects, (_, _) => { });
        var sourceId = objects.GetId(source);
        var breakpoint = Create(service, EventSpec() with { ObjectId = sourceId });
        Assert.Equal("unsupported_object", Assert.Throws<RpcException>(() => service.Update(new(breakpoint.Id,
            EventSpec() with { SourceObjectId = objects.GetId(new object()) }))).Code);
        Assert.Equal("invalid_request", Assert.Throws<RpcException>(() => Create(service,
            new(BreakpointKind.Property, "Avalonia.Controls.TextBlock:Text", SourceObjectId: sourceId))).Code);
        source.RaiseEvent(new RoutedEventArgs(Probe.Routed));
        Assert.Equal(1, Assert.Single(service.List()).HitCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long CreateTransientSource(WeakObjectRegistry objects, BreakpointService service)
    {
        var source = new Probe();
        var id = objects.GetId(source);
        Create(service, EventSpec() with { SourceObjectId = id });
        source.RaiseEvent(new RoutedEventArgs(Probe.Routed));
        return id;
    }

    private static BreakpointSpec EventSpec(RoutedEvent? routedEvent = null)
    {
        routedEvent ??= Probe.Routed;
        return new(BreakpointKind.Event, routedEvent.OwnerType.FullName + ":" + routedEvent.Name, LogMessage: false);
    }
    private static BreakpointInfo Create(BreakpointService service, BreakpointSpec definition) => service.Create(new(definition));

    private sealed class Probe : Control
    {
        public static readonly RoutedEvent<RoutedEventArgs> Routed = RoutedEvent.Register<Probe, RoutedEventArgs>("SourceProbe", RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        public static readonly RoutedEvent<RoutedEventArgs> Direct = RoutedEvent.Register<Probe, RoutedEventArgs>("DirectSourceProbe", RoutingStrategies.Direct);
    }
}
