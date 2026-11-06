using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class CapabilityLifetimeTests
{
    [AvaloniaFact]
    public void Ending_style_edits_restores_pseudo_classes_without_ending_property_overrides()
    {
        var target = new TextBlock { Opacity = 0.75 };
        var objects = new WeakObjectRegistry();
        using var observations = new ObjectObservationService(objects);
        using var properties =
            new PropertyInspectionService(Application.Current!, objects, observations, (_, _) => { });
        using var styles = new StyleInspectionService(objects, properties);
        var routes = new RpcMethodRegistry();
        properties.Register(routes);
        styles.Register(routes);
        var id = observations.Observe(target);

        routes.Invoke(Methods.PropertiesSet,
            ProtocolJson.ToElement(new SetPropertyRequest(id,
                PropertyInspectionService.PropertyId(Visual.OpacityProperty),
                "0.25")));
        routes.Invoke(ExplorationMethods.SetPseudoClass,
            ProtocolJson.ToElement(new SetPseudoClassRequest(id, ":probe", true)));
        Assert.Equal(0.25, target.Opacity);
        Assert.Contains(":probe", target.Classes);

        styles.Dispose();

        Assert.DoesNotContain(":probe", target.Classes);
        Assert.Equal(0.25, target.Opacity);
        properties.Dispose();
        Assert.Equal(0.75, target.Opacity);
    }

    [AvaloniaFact]
    public void Ending_tree_observations_preserves_property_notifications_and_restoration()
    {
        var panel = new StackPanel { Opacity = 0.75 };
        var objects = new WeakObjectRegistry();
        using var observations = new ObjectObservationService(objects);
        var notifications = new List<(string Method, object Payload)>();
        void Notify(string method, object payload) => notifications.Add((method, payload));
        using var properties = new PropertyInspectionService(Application.Current!, objects, observations, Notify);
        using var tree = new TreeInspectionService(objects, observations, () => [], Notify, "lifetime-test");
        var routes = new RpcMethodRegistry();
        properties.Register(routes);
        tree.Register(routes);
        var id = observations.Observe(panel);
        routes.Invoke(TreeWorkflowMethods.Observe, ProtocolJson.ToElement(new TreeObserveRequest(id, TreeKind.Visual)));
        routes.Invoke(Methods.PropertiesSet,
            ProtocolJson.ToElement(new SetPropertyRequest(id,
                PropertyInspectionService.PropertyId(Visual.OpacityProperty),
                "0.25")));
        properties.FlushChanges();
        notifications.Clear();

        tree.Dispose();
        panel.Children.Add(new TextBlock());
        panel.Opacity = 0.5;
        panel.IsVisible = false;
        properties.FlushChanges();

        Assert.DoesNotContain(notifications, item => item.Method == Methods.TreeInvalidated);
        Assert.Contains(notifications,
            item => item.Method == Methods.PropertiesChanged &&
                    item.Payload is PropertiesChangedNotification change && change.ObjectId == id);
        properties.Dispose();
        Assert.Equal(0.5, panel.Opacity);
    }
}
