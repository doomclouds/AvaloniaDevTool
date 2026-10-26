using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class LayoutInspectionTests
{
    [AvaloniaFact]
    public void Snapshot_preserves_parent_links_stable_ids_and_nested_layout_offsets()
    {
        var child = new Border { Name = "Leaf", Width = 80, Height = 40 };
        var nested = new Canvas { Name = "Nested", Width = 180, Height = 120, Children = { child } };
        var root = new Canvas { Name = "Root", Width = 320, Height = 240, Children = { nested } };
        Canvas.SetLeft(nested, 20);
        Canvas.SetTop(nested, 30);
        Canvas.SetLeft(child, 35);
        Canvas.SetTop(child, 40);
        using var fixture = new Fixture(root);

        var snapshot = fixture.Service.Snapshot(new LayoutSnapshotRequest(fixture.RootId));
        var repeated = fixture.Service.Snapshot(new LayoutSnapshotRequest(fixture.RootId));
        Assert.Equal("VisualGeometry", snapshot.CaptureKind);
        Assert.Equal(3, snapshot.Visited);
        Assert.False(snapshot.Truncated);
        Assert.Equal(320, snapshot.Width);
        Assert.Equal(240, snapshot.Height);
        var rootLayer = snapshot.Layers.Single(layer => layer.ObjectId == fixture.RootId);
        Assert.Null(rootLayer.ParentId);
        Assert.Equal(0, rootLayer.Depth);
        Assert.Equal(1, rootLayer.M11);
        Assert.Equal(1, rootLayer.M22);
        Assert.Equal(0, rootLayer.OffsetX);
        Assert.Equal(0, rootLayer.OffsetY);

        var nestedLayer = snapshot.Layers.Single(layer => layer.Name == "Nested");
        var leafLayer = snapshot.Layers.Single(layer => layer.Name == "Leaf");
        Assert.Equal(rootLayer.ObjectId, nestedLayer.ParentId);
        Assert.Equal(nestedLayer.ObjectId, leafLayer.ParentId);
        Assert.Equal(2, leafLayer.Depth);
        Assert.Equal(55, leafLayer.OffsetX, 8);
        Assert.Equal(70, leafLayer.OffsetY, 8);
        Assert.Equal(80, leafLayer.Width);
        Assert.Equal(40, leafLayer.Height);
        Assert.Equal(snapshot.Layers.Select(layer => layer.ObjectId), repeated.Layers.Select(layer => layer.ObjectId));
    }

    [AvaloniaFact]
    public void Snapshot_uses_real_scale_rotation_and_translation_in_root_coordinates()
    {
        var child = new Border
        {
            Name = "Transformed", Width = 80, Height = 40,
            RenderTransformOrigin = RelativePoint.TopLeft,
            RenderTransform = new MatrixTransform(new Matrix(0, 2, -3, 0, 7, 11))
        };
        var nested = new Canvas { Width = 180, Height = 120, Children = { child } };
        var root = new Canvas { Width = 320, Height = 240, Children = { nested } };
        Canvas.SetLeft(nested, 20);
        Canvas.SetTop(nested, 30);
        Canvas.SetLeft(child, 35);
        Canvas.SetTop(child, 40);
        using var fixture = new Fixture(root);

        var layer = fixture.Service.Snapshot(new LayoutSnapshotRequest(fixture.RootId)).Layers
            .Single(item => item.Name == "Transformed");
        Assert.Equal(0, layer.M11, 8);
        Assert.Equal(2, layer.M12, 8);
        Assert.Equal(-3, layer.M21, 8);
        Assert.Equal(0, layer.M22, 8);
        Assert.Equal(62, layer.OffsetX, 8);
        Assert.Equal(81, layer.OffsetY, 8);

        // The local top-right corner must preserve rotation instead of becoming an axis-aligned box.
        Assert.Equal(62, 80 * layer.M11 + layer.OffsetX, 8);
        Assert.Equal(241, 80 * layer.M12 + layer.OffsetY, 8);
    }

    [AvaloniaFact]
    public void Visibility_and_declared_clipping_include_ancestors_outside_snapshot_root()
    {
        var child = new Border { Name = "Leaf", Width = 80, Height = 40 };
        var root = new Canvas { Width = 320, Height = 240, Children = { child } };
        using var fixture = new Fixture(root);
        fixture.Host.ClipToBounds = true;
        fixture.Host.IsVisible = false;

        var snapshot = fixture.Service.Snapshot(new LayoutSnapshotRequest(fixture.RootId));
        Assert.All(snapshot.Layers, layer => Assert.False(layer.IsVisible));
        Assert.All(snapshot.Layers, layer => Assert.True(layer.IsClipped));
    }

    [AvaloniaFact]
    public void Depth_and_node_limits_report_only_actual_omissions()
    {
        var leaf = new Border { Name = "Leaf", Width = 20, Height = 20 };
        var nested = new Canvas { Width = 80, Height = 80, Children = { leaf } };
        var root = new Canvas { Width = 160, Height = 120, Children = { nested } };
        using var fixture = new Fixture(root);

        var rootOnly = fixture.Service.Snapshot(new LayoutSnapshotRequest(fixture.RootId, MaxDepth: 0));
        Assert.Single(rootOnly.Layers);
        Assert.True(rootOnly.Truncated);
        var twoNodes = fixture.Service.Snapshot(new LayoutSnapshotRequest(fixture.RootId, MaxNodes: 2));
        Assert.Equal(2, twoNodes.Visited);
        Assert.Equal(2, twoNodes.Layers.Count);
        Assert.True(twoNodes.Truncated);
        var exact = fixture.Service.Snapshot(new LayoutSnapshotRequest(fixture.RootId, MaxNodes: 3, MaxDepth: 2));
        Assert.Equal(3, exact.Visited);
        Assert.False(exact.Truncated);
    }

    [AvaloniaFact]
    public void Leaf_at_exact_limits_is_not_truncated_and_diagnostic_overlay_is_excluded()
    {
        var root = new Canvas { Width = 160, Height = 120 };
        root.Children.Add(new HighlightAdorner { Width = 40, Height = 20 });
        using var fixture = new Fixture(root);

        var snapshot = fixture.Service.Snapshot(new LayoutSnapshotRequest(fixture.RootId, MaxNodes: 1, MaxDepth: 0));
        Assert.Single(snapshot.Layers);
        Assert.Equal(1, snapshot.Visited);
        Assert.False(snapshot.Truncated);
    }

    [AvaloniaFact]
    public void Invalid_limits_expired_objects_and_unsupported_objects_fail_explicitly()
    {
        using var fixture = new Fixture(new Canvas { Width = 160, Height = 120 });
        foreach (var request in new[]
                 {
                     new LayoutSnapshotRequest(fixture.RootId, MaxNodes: 0),
                     new LayoutSnapshotRequest(fixture.RootId, MaxNodes: 257),
                     new LayoutSnapshotRequest(fixture.RootId, MaxDepth: -1),
                     new LayoutSnapshotRequest(fixture.RootId, MaxDepth: 65)
                 })
            Assert.Equal("invalid_request", Assert.Throws<RpcException>(() => fixture.Service.Snapshot(request)).Code);
        Assert.Equal("object_expired",
            Assert.Throws<RpcException>(() =>
                fixture.Service.Snapshot(new LayoutSnapshotRequest(long.MaxValue))).Code);
        var plainObjectId = fixture.Registry.GetId(new object());
        Assert.Equal("unsupported_object",
            Assert.Throws<RpcException>(() =>
                fixture.Service.Snapshot(new LayoutSnapshotRequest(plainObjectId))).Code);
    }

    [AvaloniaFact]
    public void Detached_and_zero_sized_roots_are_rejected()
    {
        var root = new Canvas { Width = 160, Height = 120 };
        using var fixture = new Fixture(root);
        fixture.Host.Children.Remove(root);
        Assert.Equal("detached_object",
            Assert.Throws<RpcException>(() =>
                fixture.Service.Snapshot(new LayoutSnapshotRequest(fixture.RootId))).Code);

        var empty = new Canvas { Width = 0, Height = 120 };
        fixture.Host.Children.Add(empty);
        fixture.Window.UpdateLayout();
        var emptyId = fixture.Registry.GetId(empty);
        Assert.Equal("invalid_layout",
            Assert.Throws<RpcException>(() =>
                fixture.Service.Snapshot(new LayoutSnapshotRequest(emptyId))).Code);
    }

    [AvaloniaFact]
    public async Task Snapshot_rejects_background_thread_access_before_reading_visual_state()
    {
        using var fixture = new Fixture(new Canvas { Width = 160, Height = 120 });
        await Task.Run(() => Assert.Throws<InvalidOperationException>(() =>
            fixture.Service.Snapshot(new LayoutSnapshotRequest(fixture.RootId))));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(Control root)
        {
            Registry = new WeakObjectRegistry();
            Service = new LayoutInspectionService(Registry, Registry.GetId);
            RootId = Registry.GetId(root);
            Host = new Canvas { Children = { root } };
            Canvas.SetLeft(root, 11);
            Canvas.SetTop(root, 17);
            Window = new Window { Width = 600, Height = 500, Content = Host };
            Window.Show();
            Window.UpdateLayout();
        }

        public WeakObjectRegistry Registry { get; }
        public LayoutInspectionService Service { get; }
        public long RootId { get; }
        public Canvas Host { get; }
        public Window Window { get; }
        public void Dispose() => Window.Close();
    }
}
