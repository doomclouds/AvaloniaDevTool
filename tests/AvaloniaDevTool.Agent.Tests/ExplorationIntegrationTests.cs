using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class ExplorationIntegrationTests
{
    [AvaloniaFact]
    public async Task Search_preserves_duplicate_names_and_ancestor_paths_identify_real_objects()
    {
        var first = new TextBlock { Name = "Duplicate", Text = "First" };
        var second = new TextBlock { Name = "Duplicate", Text = "Second" };
        var host = new StackPanel { Name = "Host", Children = { first, second } };
        await using var target = await TargetSession.CreateAsync(host);
        var results = await target.SearchAsync("Duplicate");
        Assert.Equal(2, results.Matches.Count);
        Assert.NotEqual(results.Matches[0].Id, results.Matches[1].Id);
        Assert.False(results.Truncated);
        foreach (var node in results.Matches)
        {
            var path = await target.Peer.InvokeAsync<TreeNodeInfo[]>(ExplorationMethods.Ancestors,
                new TreeAncestorsRequest(node.Id, TreeKind.Logical));
            Assert.Equal(["ExplorationRoot", "Host", "Duplicate"], path.Select(item => item.Name));
            Assert.Equal(node.Id, path[^1].Id);
        }
    }

    [AvaloniaFact]
    public async Task Search_reports_empty_results_and_explicit_work_limits()
    {
        var panel = new StackPanel { Children = { new Button { Name = "One" }, new Button { Name = "Two" } } };
        await using var target = await TargetSession.CreateAsync(panel);
        var absent = await target.SearchAsync("does-not-exist-unique");
        Assert.Empty(absent.Matches);
        Assert.False(absent.Truncated);
        var bounded = await target.Peer.InvokeAsync<TreeSearchResult>(ExplorationMethods.Search,
            new TreeSearchRequest("", TreeKind.Logical, MaxVisited: 2));
        Assert.Equal(2, bounded.Visited);
        Assert.True(bounded.Truncated);
        var limitedResults = await target.Peer.InvokeAsync<TreeSearchResult>(ExplorationMethods.Search,
            new TreeSearchRequest("", TreeKind.Logical, MaxResults: 1));
        Assert.Single(limitedResults.Matches);
        Assert.Equal(1, limitedResults.Visited);
        Assert.True(limitedResults.Truncated);
        var invalid = await Assert.ThrowsAsync<RpcException>(() => target.Peer.InvokeAsync<TreeSearchResult>(
            ExplorationMethods.Search, new TreeSearchRequest("", TreeKind.Logical, MaxResults: 0)));
        Assert.Equal("invalid_request", invalid.Code);
    }

    [AvaloniaFact]
    public async Task Paths_reject_expired_ids_and_live_objects_detached_from_tree()
    {
        var text = new TextBlock { Name = "Detachable" };
        var panel = new StackPanel { Children = { text } };
        await using var target = await TargetSession.CreateAsync(panel);
        var node = Assert.Single((await target.SearchAsync("Detachable")).Matches);
        panel.Children.Remove(text);
        var detached = await Assert.ThrowsAsync<RpcException>(() => target.Peer.InvokeAsync<TreeNodeInfo[]>(
            ExplorationMethods.Ancestors, new TreeAncestorsRequest(node.Id, TreeKind.Logical)));
        Assert.Equal("object_detached", detached.Code);
        var expired = await Assert.ThrowsAsync<RpcException>(() => target.Peer.InvokeAsync<TreeNodeInfo[]>(
            ExplorationMethods.Ancestors, new TreeAncestorsRequest(long.MaxValue, TreeKind.Visual)));
        Assert.Equal("object_expired", expired.Code);
        var expiredRoot = await Assert.ThrowsAsync<RpcException>(() => target.Peer.InvokeAsync<TreeSearchResult>(
            ExplorationMethods.Search, new TreeSearchRequest("x", TreeKind.Logical, long.MaxValue)));
        Assert.Equal("object_expired", expiredRoot.Code);
        GC.KeepAlive(text);
    }

    [AvaloniaFact]
    public async Task Element_actions_preserve_enabled_binding_and_focus_returns_actual_result()
    {
        var model = new EnabledModel { Enabled = true };
        var button = new Button { Name = "ActionTarget", Content = "Action", Width = 150, Height = 48 };
        button.Bind(InputElement.IsEnabledProperty, new Binding(nameof(EnabledModel.Enabled)) { Source = model });
        await using var target = await TargetSession.CreateAsync(button);
        var node = Assert.Single((await target.SearchAsync("ActionTarget")).Matches);
        Assert.True((await target.ActionAsync(node.Id, ElementAction.Focus)).Succeeded);
        Assert.True((await target.ActionAsync(node.Id, ElementAction.BringIntoView)).Succeeded);
        Assert.True((await target.ActionAsync(node.Id, ElementAction.Disable)).Succeeded);
        Assert.False(button.IsEnabled);
        Assert.False((await target.ActionAsync(node.Id, ElementAction.Focus)).Succeeded);
        Assert.True((await target.ActionAsync(node.Id, ElementAction.Enable)).Succeeded);
        model.Enabled = false;
        Assert.True(button.IsEnabled);
        var properties = await target.Peer.InvokeAsync<PropertyInfoDto[]>(Methods.PropertiesGet, new ObjectRequest(node.Id));
        var enabled = properties.Single(property => property.Name == "IsEnabled");
        await target.Peer.InvokeAsync<OperationResult>(Methods.PropertiesClear,
            new ClearPropertyRequest(node.Id, enabled.Id));
        Assert.False(button.IsEnabled);
        model.Enabled = true;
        Assert.True(button.IsEnabled);
    }

    [AvaloniaFact]
    public async Task Pseudo_class_changes_are_enumerable_resettable_and_reverted_on_disconnect()
    {
        var control = new PseudoProbe { Name = "PseudoTarget", Width = 60, Height = 20 };
        control.Classes.Add("business");
        ((IPseudoClasses)control.Classes).Set(":probe", true);
        await using var target = await TargetSession.CreateAsync(control);
        var node = Assert.Single((await target.SearchAsync("PseudoTarget")).Matches);
        var initial = await target.Peer.InvokeAsync<PseudoClassInfo[]>(ExplorationMethods.PseudoClasses, new ObjectRequest(node.Id));
        Assert.Contains(initial, info => info.Name == ":probe" && info.IsDeclared && info.IsActive && !info.HasOverride);
        await target.Peer.InvokeAsync<OperationResult>(ExplorationMethods.SetPseudoClass,
            new SetPseudoClassRequest(node.Id, ":probe", false));
        Assert.DoesNotContain(":probe", control.Classes);
        await target.Peer.InvokeAsync<OperationResult>(ExplorationMethods.SetPseudoClass,
            new SetPseudoClassRequest(node.Id, ":probe", true));
        await target.Peer.InvokeAsync<OperationResult>(ExplorationMethods.ResetPseudoClass,
            new ResetPseudoClassRequest(node.Id, ":probe"));
        Assert.Contains(":probe", control.Classes);
        await target.Peer.InvokeAsync<OperationResult>(ExplorationMethods.SetPseudoClass,
            new SetPseudoClassRequest(node.Id, ":temporary", true));
        var changed = await target.Peer.InvokeAsync<PseudoClassInfo[]>(ExplorationMethods.PseudoClasses, new ObjectRequest(node.Id));
        Assert.Contains(changed, info => info.Name == ":temporary" && info.IsActive && info.HasOverride && !info.IsDeclared);
        var invalid = await Assert.ThrowsAsync<RpcException>(() => target.Peer.InvokeAsync<OperationResult>(
            ExplorationMethods.SetPseudoClass, new SetPseudoClassRequest(node.Id, "business", false)));
        Assert.Equal("invalid_request", invalid.Code);
        await target.Peer.DisposeAsync();
        await WaitUntilAsync(() => !target.Agent.IsConnected);
        Assert.DoesNotContain(":temporary", control.Classes);
        Assert.Contains(":probe", control.Classes);
        Assert.Contains("business", control.Classes);
    }

    [AvaloniaFact]
    public async Task Screenshot_returns_real_png_with_exact_arranged_dimensions()
    {
        var border = new Border { Name = "ScreenshotTarget", Width = 120, Height = 64, Background = Brushes.Crimson };
        await using var target = await TargetSession.CreateAsync(border);
        var node = Assert.Single((await target.SearchAsync("ScreenshotTarget")).Matches);
        var screenshot = await target.Peer.InvokeAsync<ElementScreenshot>(ExplorationMethods.Screenshot, new ObjectRequest(node.Id));
        var bytes = Convert.FromBase64String(screenshot.PngBase64);
        Assert.Equal([137, 80, 78, 71, 13, 10, 26, 10], bytes.Take(8).Select(value => (int)value));
        Assert.Equal(120, screenshot.Width);
        Assert.Equal(64, screenshot.Height);
        Assert.Equal(screenshot.Width, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(screenshot.Height, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        Assert.InRange(bytes.Length, 24, 1024 * 1024);
    }

    [AvaloniaFact]
    public async Task Screenshot_rejects_empty_bounds_and_excessive_dimensions()
    {
        var empty = new Border { Name = "Empty", Width = 0, Height = 0 };
        var wide = new Border { Name = "Wide", Width = 2049, Height = 1 };
        var manyPixels = new Border { Name = "ManyPixels", Width = 1536, Height = 1536 };
        var canvas = new Canvas { Children = { empty, wide, manyPixels } };
        await using var target = await TargetSession.CreateAsync(canvas);
        foreach (var (name, code) in new[] { ("Empty", "invalid_bounds"), ("Wide", "screenshot_limit"), ("ManyPixels", "screenshot_limit") })
        {
            var node = (await target.SearchAsync(name)).Matches.Single(item => item.Name == name);
            var error = await Assert.ThrowsAsync<RpcException>(() => target.Peer.InvokeAsync<ElementScreenshot>(
                ExplorationMethods.Screenshot, new ObjectRequest(node.Id)));
            Assert.Equal(code, error.Code);
        }
    }

    [AvaloniaFact]
    public async Task Screenshot_rejects_png_over_body_limit_without_downscaling()
    {
        using var bitmap = CreateNoiseBitmap(1024, 1024);
        var image = new Image { Name = "NoiseTarget", Width = 1024, Height = 1024, Source = bitmap, Stretch = Stretch.Fill };
        await using var target = await TargetSession.CreateAsync(new Canvas { Children = { image } });
        var node = Assert.Single((await target.SearchAsync("NoiseTarget")).Matches);
        var error = await Assert.ThrowsAsync<RpcException>(() => target.Peer.InvokeAsync<ElementScreenshot>(
            ExplorationMethods.Screenshot, new ObjectRequest(node.Id)));
        Assert.Equal("screenshot_limit", error.Code);
        Assert.Equal(1024, image.Bounds.Width);
        Assert.Equal(1024, image.Bounds.Height);
    }

    private static WriteableBitmap CreateNoiseBitmap(int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Opaque);
        using var framebuffer = bitmap.Lock();
        var data = new byte[framebuffer.RowBytes * height];
        new Random(783).NextBytes(data);
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
                data[row * framebuffer.RowBytes + column * 4 + 3] = 255;
        Marshal.Copy(data, 0, framebuffer.Address, data.Length);
        return bitmap;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
            await Task.Delay(15, timeout.Token);
    }

    [PseudoClasses(":probe")]
    private sealed class PseudoProbe : Control;

    private sealed class EnabledModel : INotifyPropertyChanged
    {
        private bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class TargetSession(Window window, DiagnosticsAgent agent, RpcPeer peer) : IAsyncDisposable
    {
        public DiagnosticsAgent Agent { get; } = agent;
        public RpcPeer Peer { get; } = peer;
        public static async Task<TargetSession> CreateAsync(Control content)
        {
            var window = new Window { Name = "ExplorationRoot", Width = 400, Height = 300, Content = content };
            window.Show();
            window.UpdateLayout();
            var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            {
                PipeName = "exploration-tests-" + Guid.NewGuid().ToString("N"),
                ApplicationName = "Exploration tests",
                TopLevelProvider = () => [window]
            });
            var pipe = new NamedPipeClientStream(".", agent.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            var peer = new RpcPeer(pipe);
            peer.Start();
            await peer.InvokeAsync<SessionInfo>(Methods.SessionInfo, null);
            return new TargetSession(window, agent, peer);
        }
        public Task<TreeSearchResult> SearchAsync(string query) => Peer.InvokeAsync<TreeSearchResult>(
            ExplorationMethods.Search, new TreeSearchRequest(query, TreeKind.Logical));
        public Task<ElementActionResult> ActionAsync(long id, ElementAction action) => Peer.InvokeAsync<ElementActionResult>(
            ExplorationMethods.Action, new ElementActionRequest(id, action));
        public async ValueTask DisposeAsync()
        {
            await Peer.DisposeAsync();
            await Agent.DisposeAsync();
            window.Close();
        }
    }
}

