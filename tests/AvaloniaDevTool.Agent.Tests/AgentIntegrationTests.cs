using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class AgentIntegrationTests
{
    [AvaloniaFact]
    public async Task Named_pipe_session_exposes_roots_children_and_property_values()
    {
        var text = new TextBlock { Name = "Message", Text = "Original" };
        var panel = new StackPanel { Name = "Host", Children = { text } };
        await using var connection = await TestConnection.CreateAsync(panel);
        var info = await connection.Peer.InvokeAsync<SessionInfo>(Methods.SessionInfo, null);
        Assert.Equal(DiagnosticsProtocol.Version, info.ProtocolVersion);
        var roots = await connection.Peer.InvokeAsync<TreeNodeInfo[]>(Methods.TreeRoots, new TreeRootsRequest(TreeKind.Logical));
        Assert.Single(roots);
        var children = await connection.Peer.InvokeAsync<TreeNodeInfo[]>(Methods.TreeChildren,
            new TreeChildrenRequest(roots[0].Id, TreeKind.Logical));
        Assert.Contains(children, node => node.Name == "Host");
        var panelNode = children.Single(node => node.Name == "Host");
        var controls = await connection.Peer.InvokeAsync<TreeNodeInfo[]>(Methods.TreeChildren,
            new TreeChildrenRequest(panelNode.Id, TreeKind.Logical));
        var textNode = controls.Single(node => node.Name == "Message");
        Assert.Equal("Original", textNode.Preview);
        var properties = await connection.GetPropertiesAsync(textNode.Id);
        Assert.Contains(properties, property => property.Name == "Text" && property.Value == "Original");
        Assert.Contains(properties, property => property.Name == "Bounds" && property.IsReadOnly);
    }

    [AvaloniaFact]
    public async Task Diagnostic_override_preserves_live_binding_and_clear_reveals_latest_value()
    {
        var model = new MessageModel { Text = "Before" };
        var text = new TextBlock { Name = "Message" };
        text.Bind(TextBlock.TextProperty, new Binding(nameof(MessageModel.Text)) { Source = model });
        await using var connection = await TestConnection.CreateAsync(text);
        var nodeId = await connection.GetContentIdAsync();
        var property = (await connection.GetPropertiesAsync(nodeId)).Single(x => x.Name == "Text");
        await connection.Peer.InvokeAsync<OperationResult>(Methods.PropertiesSet,
            new SetPropertyRequest(nodeId, property.Id, "Inspection override"));
        Assert.Equal("Inspection override", text.Text);
        await connection.Peer.InvokeAsync<OperationResult>(Methods.PropertiesSet,
            new SetPropertyRequest(nodeId, property.Id, "Replacement override"));
        Assert.Equal("Replacement override", text.Text);
        model.Text = "Updated while overridden";
        Assert.Equal("Replacement override", text.Text);
        var overridden = (await connection.GetPropertiesAsync(nodeId)).Single(x => x.Id == property.Id);
        Assert.Equal("DiagnosticOverride", overridden.ValueSource);
        Assert.True(overridden.CanClear);
        await connection.Peer.InvokeAsync<OperationResult>(Methods.PropertiesClear, new ClearPropertyRequest(nodeId, property.Id));
        Assert.Equal("Updated while overridden", text.Text);
        model.Text = "Binding still connected";
        Assert.Equal("Binding still connected", text.Text);
    }

    [AvaloniaFact]
    public async Task Invalid_and_readonly_edits_return_errors_without_changing_target()
    {
        var text = new TextBlock { Name = "Message", Width = 120 };
        await using var connection = await TestConnection.CreateAsync(text);
        var nodeId = await connection.GetContentIdAsync();
        var properties = await connection.GetPropertiesAsync(nodeId);
        var bounds = properties.Single(x => x.Name == "Bounds");
        var error = await Assert.ThrowsAsync<RpcException>(() =>
            connection.Peer.InvokeAsync<OperationResult>(Methods.PropertiesSet, new SetPropertyRequest(nodeId, bounds.Id, "1")));
        Assert.Equal("readonly_property", error.Code);
        var width = properties.Single(x => x.Name == "Width");
        error = await Assert.ThrowsAsync<RpcException>(() =>
            connection.Peer.InvokeAsync<OperationResult>(Methods.PropertiesSet, new SetPropertyRequest(nodeId, width.Id, "not a number")));
        Assert.Equal("conversion_failed", error.Code);
        Assert.Equal(120, text.Width);
        error = await Assert.ThrowsAsync<RpcException>(() =>
            connection.Peer.InvokeAsync<OperationResult>(Methods.PropertiesSet, new SetPropertyRequest(nodeId, "missing", "1")));
        Assert.Equal("property_not_found", error.Code);
    }

    [AvaloniaFact]
    public async Task Disconnect_removes_overrides_and_new_connection_has_a_new_session()
    {
        var text = new TextBlock { Text = "Original" };
        await using var connection = await TestConnection.CreateAsync(text);
        var firstSession = await connection.Peer.InvokeAsync<SessionInfo>(Methods.SessionInfo, null);
        var nodeId = await connection.GetContentIdAsync();
        var property = (await connection.GetPropertiesAsync(nodeId)).Single(x => x.Name == "Text");
        await connection.Peer.InvokeAsync<OperationResult>(Methods.PropertiesSet, new SetPropertyRequest(nodeId, property.Id, "Temporary"));
        Assert.Equal("Temporary", text.Text);
        await connection.Peer.DisposeAsync();
        await WaitUntilAsync(() => !connection.Agent.IsConnected && text.Text == "Original");
        Assert.Equal("Original", text.Text);
        await using var secondPipe = new NamedPipeClientStream(".", connection.Agent.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await secondPipe.ConnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await using var secondPeer = new RpcPeer(secondPipe);
        secondPeer.Start();
        var secondSession = await secondPeer.InvokeAsync<SessionInfo>(Methods.SessionInfo, null);
        Assert.NotEqual(firstSession.SessionId, secondSession.SessionId);
    }

    [AvaloniaFact]
    public async Task Changing_materialized_children_invalidates_their_parent()
    {
        var panel = new StackPanel { Name = "Host" };
        await using var connection = await TestConnection.CreateAsync(panel);
        var panelId = await connection.GetContentIdAsync();
        await connection.Peer.InvokeAsync<TreeNodeInfo[]>(Methods.TreeChildren, new TreeChildrenRequest(panelId, TreeKind.Logical));
        var received = new TaskCompletionSource<long?>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Peer.NotificationReceived += (method, payload) =>
        {
            if (method == Methods.TreeInvalidated)
            {
                var notification = ProtocolJson.Read<TreeInvalidatedNotification>(payload);
                if (notification.ParentId == panelId)
                    received.TrySetResult(notification.ParentId);
            }
        };
        panel.Children.Add(new Button { Name = "Added", Content = "New" });
        Assert.Equal(panelId, await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [AvaloniaFact]
    public async Task Attached_properties_can_be_overridden_and_reverted()
    {
        var text = new TextBlock { Text = "Grid child" };
        Grid.SetRow(text, 2);
        await using var connection = await TestConnection.CreateAsync(text);
        var nodeId = await connection.GetContentIdAsync();
        var row = (await connection.GetPropertiesAsync(nodeId))
            .Single(property => property.Name == "Row" && property.DeclaringType == typeof(Grid).FullName);
        await connection.Peer.InvokeAsync<OperationResult>(Methods.PropertiesSet, new SetPropertyRequest(nodeId, row.Id, "4"));
        Assert.Equal(4, Grid.GetRow(text));
        await connection.Peer.InvokeAsync<OperationResult>(Methods.PropertiesClear, new ClearPropertyRequest(nodeId, row.Id));
        Assert.Equal(2, Grid.GetRow(text));
    }

    [AvaloniaFact]
    public async Task Open_popup_content_is_reachable_from_visual_roots()
    {
        var anchor = new Button { Content = "Popup anchor" };
        var popup = new Popup
        {
            Child = new TextBlock { Name = "PopupContent", Text = "Popup text" },
            PlacementTarget = anchor
        };
        var panel = new StackPanel { Children = { anchor, popup } };
        await using var connection = await TestConnection.CreateAsync(panel);
        popup.IsOpen = true;
        try
        {
            var roots = await connection.Peer.InvokeAsync<TreeNodeInfo[]>(Methods.TreeRoots,
                new TreeRootsRequest(TreeKind.Visual));
            var pending = new Queue<TreeNodeInfo>(roots);
            var found = false;
            var visited = new HashSet<long>();
            while (pending.TryDequeue(out var node) && visited.Count < 500)
            {
                if (!visited.Add(node.Id)) continue;
                if (node.Name == "PopupContent") { found = true; break; }
                if (!node.HasChildren) continue;
                var children = await connection.Peer.InvokeAsync<TreeNodeInfo[]>(Methods.TreeChildren,
                    new TreeChildrenRequest(node.Id, TreeKind.Visual));
                foreach (var child in children) pending.Enqueue(child);
            }
            Assert.True(found);
        }
        finally { popup.IsOpen = false; }
    }

    [Fact]
    public void Registry_does_not_keep_inspected_objects_alive()
    {
        var registry = new WeakObjectRegistry();
        var id = RegisterTransient(registry);
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(registry.TryGet(id, out _));
        var error = Assert.Throws<RpcException>(() => registry.Get(id));
        Assert.Equal("object_expired", error.Code);
    }

    [AvaloniaFact]
    public async Task Picking_uses_real_pointer_input_highlights_selection_and_does_not_click_button()
    {
        var clicks = 0;
        var button = new Button { Name = "PickTarget", Content = "Pick this", Width = 180, Height = 64 };
        button.Click += (_, _) => clicks++;
        await using var connection = await TestConnection.CreateAsync(button);
        var nodeId = await connection.GetContentIdAsync();
        await connection.Peer.InvokeAsync<OperationResult>(Methods.Highlight, new HighlightRequest(nodeId));
        var layer = AdornerLayer.GetAdornerLayer(button);
        Assert.NotNull(layer);
        Assert.Contains(layer.Children, control => control is HighlightAdorner);

        var picked = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Peer.NotificationReceived += (method, payload) =>
        {
            if (method == Methods.SelectionChanged)
                picked.TrySetResult(ProtocolJson.Read<SelectionChangedNotification>(payload).ObjectId);
            if (method == Methods.PickingChanged && !ProtocolJson.Read<PickingChangedNotification>(payload).IsPicking)
                stopped.TrySetResult();
        };
        await connection.Peer.InvokeAsync<OperationResult>(Methods.SetPicking, new PickingRequest(true));
        var point = button.TranslatePoint(new Point(10, 10), connection.Window);
        Assert.NotNull(point);
        connection.Window.MouseMove(point.Value);
        connection.Window.MouseDown(point.Value, MouseButton.Left);
        connection.Window.MouseUp(point.Value, MouseButton.Left);
        var pickedId = await picked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(await connection.GetPropertiesAsync(pickedId));
        Assert.Equal(0, clicks);
        Assert.Contains(layer.Children, control => control is HighlightAdorner);
        await connection.Peer.DisposeAsync();
        await WaitUntilAsync(() => !connection.Agent.IsConnected);
        Assert.DoesNotContain(layer.Children, control => control is HighlightAdorner);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RegisterTransient(WeakObjectRegistry registry) => registry.GetId(new object());

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < end)
            await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class MessageModel : INotifyPropertyChanged
    {
        private string _text = "";
        public string Text
        {
            get => _text;
            set { _text = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private sealed class TestConnection : IAsyncDisposable
    {
        private readonly Window _window;
        private TestConnection(Window window, DiagnosticsAgent agent, RpcPeer peer)
        {
            _window = window; Agent = agent; Peer = peer;
        }
        public DiagnosticsAgent Agent { get; }
        public RpcPeer Peer { get; }
        public Window Window => _window;

        public static async Task<TestConnection> CreateAsync(Control content)
        {
            content.Name ??= "TestContent";
            var window = new Window { Width = 500, Height = 400, Content = content };
            window.Show();
            var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            {
                ApplicationName = "Agent integration tests",
                PipeName = "avdt-test-" + Guid.NewGuid().ToString("N"),
                TopLevelProvider = () => [window]
            });
            var pipe = new NamedPipeClientStream(".", agent.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            var peer = new RpcPeer(pipe);
            peer.Start();
            await peer.InvokeAsync<SessionInfo>(Methods.SessionInfo, null);
            return new TestConnection(window, agent, peer);
        }

        public Task<PropertyInfoDto[]> GetPropertiesAsync(long id) =>
            Peer.InvokeAsync<PropertyInfoDto[]>(Methods.PropertiesGet, new ObjectRequest(id));

        public async Task<long> GetContentIdAsync()
        {
            var roots = await Peer.InvokeAsync<TreeNodeInfo[]>(Methods.TreeRoots, new TreeRootsRequest(TreeKind.Logical));
            var children = await Peer.InvokeAsync<TreeNodeInfo[]>(Methods.TreeChildren,
                new TreeChildrenRequest(roots[0].Id, TreeKind.Logical));
            return children.Single(node => node.Name == ((Control)_window.Content!).Name).Id;
        }

        public async ValueTask DisposeAsync()
        {
            await Peer.DisposeAsync();
            await Agent.DisposeAsync();
            _window.Close();
        }
    }
}

