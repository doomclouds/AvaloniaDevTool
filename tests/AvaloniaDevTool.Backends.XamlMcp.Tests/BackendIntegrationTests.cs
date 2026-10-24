using System.Diagnostics;
using AvaloniaDevTool.Backends.XamlMcp;
using AvaloniaDevTool.Protocol;
using Xunit;
using Xunit.Abstractions;

namespace AvaloniaDevTool.Backends.XamlMcp.Tests;

public sealed class BackendIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Published_packages_drive_tree_properties_binding_png_and_detach_through_adapter()
    {
        Assert.True(File.Exists(XamlMcpServerLocation.BundledAssemblyPath), XamlMcpServerLocation.BundledAssemblyPath);
        await using var target = await TargetFixture.StartAsync(output);
        Assert.StartsWith("12.1.4", target.AvaloniaVersion);
        Assert.StartsWith("1.0.0-preview.3", target.AgentVersion);
        await using var backend = new XamlMcpBackend(new XamlMcpBackendOptions { DiscoveryDirectory = target.DiscoveryDirectory });
        var states = new List<bool>();
        backend.ConnectionChanged += () => states.Add(backend.IsConnected);
        var applications = await backend.ListApplicationsAsync();
        Assert.Contains(applications, app => app.Pid == target.Pid);
        Assert.False(backend.IsConnected);
        await backend.ConnectAsync("pid:" + target.Pid);
        target.RecordServer(backend.LastServerProcessId);
        Assert.True(backend.IsConnected);
        Assert.Equal(target.Pid, backend.Session!.ProcessId);
        Assert.False(backend.HasStableObjectIds);
        Assert.False(backend.HasPushUpdates);
        Assert.Equal("unsupported_tree", (await Assert.ThrowsAsync<RpcException>(() => backend.GetRootsAsync(TreeKind.Merged))).Code);
        var roots = await backend.GetRootsAsync(TreeKind.Visual);
        var all = await WalkAsync(backend, roots, TreeKind.Visual);
        var healthy = all.Single(node => node.Name == "HealthyBindingTarget");
        Assert.Equal("unsupported_tree", (await Assert.ThrowsAsync<RpcException>(() => backend.GetChildrenAsync(healthy.Id, TreeKind.Merged))).Code);
        Assert.Equal("unsupported_tree", (await Assert.ThrowsAsync<RpcException>(() => backend.GetAncestorsAsync(healthy.Id, TreeKind.Merged))).Code);
        var properties = await backend.GetPropertiesAsync(healthy.Id);
        Assert.Contains(properties, property => property.Name == "Text" && property.Value == "来自开源 Agent 的绑定文本");
        Assert.All(properties, property => { Assert.False(property.CanEdit); Assert.False(property.CanClear); });
        var styles = await backend.GetStylesAsync(healthy.Id);
        Assert.Contains("XamlMcp", styles.Adapter);
        Assert.Contains(styles.Bindings, binding => binding.PropertyName == "Text" && binding.Expression.Contains("Message"));
        var bindingDetails = Assert.Single(styles.Bindings, binding => binding.PropertyName == "Text").Details!;
        Assert.NotNull(bindingDetails);
        Assert.Equal(BindingDetailState.Unavailable, bindingDetails.Mode.State);
        Assert.Null(bindingDetails.Source.ObjectId);
        Assert.Contains(bindingDetails.Gaps, gap => gap.Field == "bindings.mode");
        Assert.All(styles.Frames.SelectMany(frame => frame.Setters), setter => Assert.Null(setter.MatchesEffectiveValue));
        Assert.NotEmpty(styles.Limitations!);
        var broken = all.Single(node => node.Name == "BrokenBindingTarget");
        var brokenStyles = await backend.GetStylesAsync(broken.Id);
        Assert.Contains(brokenStyles.Bindings, binding => binding.ErrorType.Contains("path-error"));
        Assert.Contains(brokenStyles.Bindings.SelectMany(binding => binding.Details!.Errors), error => error.Category == "path-error");
        var path = await backend.GetAncestorsAsync(healthy.Id, TreeKind.Visual);
        Assert.Equal(roots[0].Id, path[0].Id);
        Assert.Equal(healthy.Id, path[^1].Id);
        var image = await backend.CaptureElementAsync(healthy.Id);
        Assert.True(image.Width > 0 && image.Height > 0);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, Convert.FromBase64String(image.PngBase64)[..8]);
        var previousSnapshot = backend.CurrentSnapshotId;
        var newRoots = await backend.GetRootsAsync(TreeKind.Visual);
        var error = await Assert.ThrowsAsync<RpcException>(() => backend.GetPropertiesAsync(healthy.Id));
        Assert.Equal("snapshot_expired", error.Code);
        Assert.NotEqual(previousSnapshot, backend.CurrentSnapshotId);
        Assert.NotEqual(roots[0].Id, newRoots[0].Id);
        await backend.DisconnectAsync();
        target.RecordServer(backend.LastServerProcessId);
        Assert.False(backend.IsConnected);
        Assert.Null(backend.Session);
        Assert.Equal(new[] { true, false }, states);
        Assert.True(HasExited(backend.LastServerProcessId));
        Assert.Equal("not_connected", (await Assert.ThrowsAsync<RpcException>(() => backend.GetRootsAsync(TreeKind.Visual))).Code);
    }

    [Fact]
    public async Task Instance_attachment_preserves_logical_parent_path_and_reports_truncated_branches()
    {
        await using var target = await TargetFixture.StartAsync(output);
        await using var backend = new XamlMcpBackend(new XamlMcpBackendOptions
        {
            DiscoveryDirectory = target.DiscoveryDirectory, MaxSnapshotNodes = 3, SnapshotDepth = 2
        });
        var instance = (await backend.ListApplicationsAsync()).Single(app => app.Pid == target.Pid);
        await backend.ConnectAsync("instance:" + instance.InstanceId);
        target.RecordServer(backend.LastServerProcessId);
        var roots = await backend.GetRootsAsync(TreeKind.Logical);
        Assert.Contains("截断", backend.LastNotice);
        var cursor = roots[0];
        var truncated = false;
        var pending = new Queue<TreeNodeInfo>(roots);
        while (pending.TryDequeue(out var candidate))
        {
            if (!candidate.HasChildren) continue;
            cursor = candidate;
            try
            {
                var children = await backend.GetChildrenAsync(cursor.Id, TreeKind.Logical);
                foreach (var child in children) pending.Enqueue(child);
            }
            catch (RpcException exception) when (exception.Code == "snapshot_truncated") { truncated = true; break; }
        }
        Assert.True(truncated, backend.LastNotice);
        var ancestors = await backend.GetAncestorsAsync(cursor.Id, TreeKind.Logical);
        Assert.Equal(roots[0].Id, ancestors[0].Id);
        Assert.Equal(cursor.Id, ancestors[^1].Id);
        Assert.Contains("logical", backend.LastNotice);
        Assert.Equal("tree_scope_mismatch",
            (await Assert.ThrowsAsync<RpcException>(() => backend.GetAncestorsAsync(cursor.Id, TreeKind.Visual))).Code);
        await backend.DisconnectAsync();
        target.RecordServer(backend.LastServerProcessId);
        Assert.True(HasExited(backend.LastServerProcessId));
    }

    private static async Task<IReadOnlyList<TreeNodeInfo>> WalkAsync(XamlMcpBackend backend,
        IReadOnlyList<TreeNodeInfo> roots, TreeKind kind)
    {
        var result = new List<TreeNodeInfo>();
        var pending = new Queue<TreeNodeInfo>(roots);
        while (pending.TryDequeue(out var node))
        {
            result.Add(node);
            if (node.HasChildren)
                foreach (var child in await backend.GetChildrenAsync(node.Id, kind)) pending.Enqueue(child);
        }
        return result;
    }

    private static bool HasExited(int? pid)
    {
        if (pid is null) return false;
        try { using var process = Process.GetProcessById(pid.Value); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
}

