using AvaloniaDevTool.Backends.XamlMcp;
using AvaloniaDevTool.Protocol;
using Xunit;
using Upstream = global::XamlMcp.Protocol;

namespace AvaloniaDevTool.Backends.XamlMcp.Tests;

public sealed class SnapshotCacheTests
{
    [Fact]
    public void Same_native_node_id_in_new_snapshot_gets_new_handle_and_old_handle_expires()
    {
        var cache = new XamlSnapshotCache();
        var first = cache.Load(Tree(101), TreeKind.Visual, 10);
        var old = Assert.Single(cache.Children(first[0].Id, TreeKind.Visual));
        Assert.Equal(101, cache.Resolve(old.Id).Target.SnapshotId);
        Assert.Equal(2, cache.Resolve(old.Id).Target.NodeId);
        var second = cache.Load(Tree(102), TreeKind.Visual, 10);
        var current = Assert.Single(cache.Children(second[0].Id, TreeKind.Visual));
        Assert.NotEqual(old.Id, current.Id);
        var error = Assert.Throws<RpcException>(() => cache.Resolve(old.Id));
        Assert.Equal("snapshot_expired", error.Code);
        Assert.Equal(102, cache.Resolve(current.Id).Target.SnapshotId);
        Assert.Equal(2, cache.Resolve(current.Id).Target.NodeId);
    }

    [Fact]
    public void Truncated_children_are_not_reported_as_an_empty_or_complete_branch()
    {
        var cache = new XamlSnapshotCache();
        var root = new Upstream.TreeNodeDto(1, "Window", "Root", 2, null,
            [new Upstream.TreeNodeDto(2, "Button", "Loaded", 0, null, null)]);
        var roots = cache.Load(new Upstream.TreeResult(1, [root], true, 2), TreeKind.Logical, 4);
        Assert.True(roots[0].HasChildren);
        Assert.Equal("snapshot_truncated", Assert.Throws<RpcException>(() => cache.Children(roots[0].Id, TreeKind.Logical)).Code);
    }

    [Fact]
    public void Logical_ancestors_use_logical_snapshot_parents_and_reject_other_scopes()
    {
        var cache = new XamlSnapshotCache();
        var roots = cache.Load(Tree(101), TreeKind.Logical, 10);
        var child = Assert.Single(cache.Children(roots[0].Id, TreeKind.Logical));
        Assert.Equal(new[] { roots[0].Id, child.Id }, cache.LocalAncestors(child.Id, TreeKind.Logical).Select(node => node.Id));
        Assert.Equal("tree_scope_mismatch", Assert.Throws<RpcException>(() => cache.LocalAncestors(child.Id, TreeKind.Visual)).Code);
    }

    [Fact]
    public void Cache_limit_is_enforced_before_publishing_a_new_snapshot()
    {
        var cache = new XamlSnapshotCache();
        var roots = cache.Load(Tree(101), TreeKind.Visual, 10);
        Assert.Equal("invalid_response", Assert.Throws<RpcException>(() => cache.Load(Tree(102), TreeKind.Visual, 1)).Code);
        Assert.Equal(101, cache.SnapshotId);
        Assert.Equal(2, cache.Count);
        Assert.NotNull(cache.Resolve(roots[0].Id));
    }

    private static Upstream.TreeResult Tree(long snapshot) => new(snapshot,
        [new Upstream.TreeNodeDto(1, "Window", "Root", 1, null,
            [new Upstream.TreeNodeDto(2, "TextBlock", "BoundText", 0,
                new Dictionary<string, string> { ["text"] = "Snapshot " + snapshot }, null)])], false, 2);
}

