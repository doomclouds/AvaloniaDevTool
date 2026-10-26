using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class AgentInteractionBoundaryMcpTests
{
    private static string CliPath => typeof(AgentInteractionBoundaryMcpTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "PreviewCliPath").Value!;

    [Fact]
    public async Task Default_stdio_tools_list_has_exactly_the_44_independent_agent_tool_names()
    {
        await using var client = await CreateClientAsync();
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        string[] expected =
        [
            "action", "ancestors", "assets", "attach-to-app", "attach-to-file", "detach",
            "capture-start", "capture-status", "capture-stop",
            "file-dialog-accept", "file-dialog-cancel", "file-dialog-inspect", "file-dialog-overwrite",
            "file-dialog-set-filter", "file-dialog-set-paths", "file-dialog-wait", "file-dialogs",
            "input", "native-action", "native-action-status", "native-find", "native-input", "native-inspect",
            "native-tree", "native-windows", "native-screenshot", "open-asset", "props", "pseudo-class", "resources",
            "screenshot",
            "search", "set-prop", "styles", "tree", "ui-click", "ui-control-action", "ui-drag", "ui-input",
            "ui-key-chord", "ui-select-item", "ui-text-selection", "ui-wait", "ui-screenshot"
        ];
        Assert.Equal(44, tools.Count);
        Assert.Equal(expected.Order(StringComparer.Ordinal),
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));
    }

    [AvaloniaTheory]
    [InlineData("hidden-ancestor", "hidden_target")]
    [InlineData("disabled-ancestor", "disabled_target")]
    [InlineData("detached", "detached_object")]
    [InlineData("zero-layout", "invalid_layout")]
    public async Task Stdio_invalid_target_states_fail_before_any_routed_input_or_business_action(string state,
        string code)
    {
        var button = new Button { Name = "BoundaryGuarded", Width = 180, Height = 60, Content = "Unchanged" };
        var parent = new Border { Width = 200, Height = 80, Child = button };
        var target = new Border
            { Name = "BoundaryHealthyTarget", Width = 100, Height = 60, Background = Brushes.White };
        using var fixture = new Fixture(parent, target);
        await using var agent = fixture.Attach();
        await using var client = await CreateClientAsync(agent.PipeName);
        var id = await Find(client, button.Name!);
        var targetId = await Find(client, target.Name!);
        Assert.True(button.Bounds.Width > 0 && button.Bounds.Height > 0);
        using var probe = new InputProbe(button);
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        switch (state)
        {
            case "hidden-ancestor":
                parent.IsVisible = false;
                break;
            case "disabled-ancestor":
                parent.IsEnabled = false;
                break;
            case "detached":
                parent.Child = null;
                break;
            case "zero-layout":
                button.Width = 0;
                break;
        }

        fixture.Window.UpdateLayout();
        if (state == "zero-layout")
            Assert.Equal(0, button.Bounds.Width);

        await Error(client, "ui-click", code, ("nodeId", id));
        await Error(client, "ui-input", code, ("nodeId", id), ("kind", "Text"), ("text", "blocked"));
        await Error(client, "ui-control-action", code, ("nodeId", id), ("action", "Invoke"));
        await Error(client, "ui-drag", code, ("sourceNodeId", id), ("targetNodeId", targetId));
        Assert.Empty(probe.Events);
        Assert.Null(probe.Pointer);
        Assert.Equal(0, clicks);
        Assert.Equal("Unchanged", button.Content);
        Assert.False(button.IsPressed);
    }

    [AvaloniaFact]
    public async Task Stdio_readonly_and_unfocusable_targets_fail_without_text_key_or_selection_side_effects()
    {
        var text = new TextBox
            { Name = "BoundaryReadOnly", Width = 220, Height = 60, Text = "Original text", IsReadOnly = true };
        var unfocusable = new Border
            { Name = "BoundaryUnfocusable", Width = 180, Height = 60, Focusable = false, Background = Brushes.White };
        using var fixture = new Fixture(text, unfocusable);
        await using var agent = fixture.Attach();
        await using var client = await CreateClientAsync(agent.PipeName);
        var textId = await Find(client, text.Name!);
        var unfocusableId = await Find(client, unfocusable.Name!);
        text.SelectionStart = 2;
        text.SelectionEnd = 5;
        var selection = (text.SelectionStart, text.SelectionEnd, text.CaretIndex);
        using var textProbe = new InputProbe(text);
        using var focusProbe = new InputProbe(unfocusable);
        var changes = 0;
        text.TextChanged += (_, _) => changes++;

        await Error(client, "ui-input", "readonly_target", ("nodeId", textId), ("kind", "Text"), ("text", "blocked"));
        await Error(client,
            "ui-text-selection",
            "readonly_target",
            ("nodeId", textId),
            ("start", 0),
            ("length", 8),
            ("replacementText", "blocked"));
        await Error(client,
            "ui-input",
            "focus_failed",
            ("nodeId", unfocusableId),
            ("kind", "Text"),
            ("text", "blocked"));
        await Error(client, "ui-input", "focus_failed", ("nodeId", unfocusableId), ("kind", "KeyDown"), ("key", "A"));
        await Error(client,
            "ui-key-chord",
            "focus_failed",
            ("nodeId", unfocusableId),
            ("keys", new[] { "A" }),
            ("modifiers", new[] { "Control" }));
        Assert.Empty(textProbe.Events);
        Assert.Empty(focusProbe.Events);
        Assert.Equal("Original text", text.Text);
        Assert.Equal(selection, (text.SelectionStart, text.SelectionEnd, text.CaretIndex));
        Assert.Equal(0, changes);
        Assert.False(unfocusable.IsFocused);
    }

    [AvaloniaTheory]
    [InlineData(-1d, 10d)]
    [InlineData(10d, -1d)]
    [InlineData(181d, 10d)]
    [InlineData(10d, 61d)]
    [InlineData(double.MaxValue, 10d)]
    public async Task Stdio_invalid_local_coordinates_fail_before_pointer_events_or_capture(double x, double y)
    {
        var source = new Button { Name = "BoundaryCoordinates", Width = 180, Height = 60 };
        var target = new Border
            { Name = "BoundaryCoordinateTarget", Width = 100, Height = 60, Background = Brushes.White };
        using var fixture = new Fixture(source, target);
        await using var agent = fixture.Attach();
        await using var client = await CreateClientAsync(agent.PipeName);
        var sourceId = await Find(client, source.Name!);
        var targetId = await Find(client, target.Name!);
        using var probe = new InputProbe(source);
        using var targetProbe = new InputProbe(target);
        var clicks = 0;
        source.Click += (_, _) => clicks++;

        await Error(client, "ui-click", "invalid_coordinates", ("nodeId", sourceId), ("x", x), ("y", y));
        foreach (var kind in new[] { "PointerMove", "PointerDown", "Click" })
            await Error(client,
                "ui-input",
                "invalid_coordinates",
                ("nodeId", sourceId),
                ("kind", kind),
                ("x", x),
                ("y", y));
        await Error(client,
            "ui-drag",
            "invalid_coordinates",
            ("sourceNodeId", sourceId),
            ("targetNodeId", targetId),
            ("path", new[] { new { nodeId = sourceId, x, y } }));
        Assert.Empty(probe.Events);
        Assert.Empty(targetProbe.Events);
        Assert.Null(probe.Pointer);
        Assert.Equal(0, clicks);
        Assert.False(source.IsPressed);
    }

    [AvaloniaFact]
    public async Task
        Stdio_cross_root_drag_endpoints_and_waypoints_fail_without_partial_down_or_capture_then_allow_a_valid_drag()
    {
        var source = new Button { Name = "BoundaryCrossRootSource", Width = 180, Height = 60 };
        var target = new Border
            { Name = "BoundaryCrossRootTarget", Width = 100, Height = 60, Background = Brushes.White };
        var other = new Border { Name = "BoundaryOtherRoot", Width = 100, Height = 60, Background = Brushes.White };
        using var fixture = new Fixture(source, target);
        using var second = new Fixture(other);
        await using var agent = fixture.Attach(second.Window);
        await using var client = await CreateClientAsync(agent.PipeName);
        var sourceId = await Find(client, source.Name!);
        var targetId = await Find(client, target.Name!);
        var otherId = await Find(client, other.Name!);
        using var sourceProbe = new InputProbe(source);
        using var targetProbe = new InputProbe(target);
        using var otherProbe = new InputProbe(other);
        Read<InputResult>(await Call(client, "ui-input", ("nodeId", sourceId), ("kind", "PointerMove")));
        var pointer = Assert.IsAssignableFrom<IPointer>(sourceProbe.Pointer);
        sourceProbe.Events.Clear();

        await Error(client, "ui-drag", "cross_root_input", ("sourceNodeId", sourceId), ("targetNodeId", otherId));
        await Error(client,
            "ui-drag",
            "cross_root_input",
            ("sourceNodeId", sourceId),
            ("targetNodeId", targetId),
            ("path",
                new[] { new { nodeId = targetId, x = 10d, y = 10d }, new { nodeId = otherId, x = 10d, y = 10d } }));
        Assert.Empty(sourceProbe.Events);
        Assert.Empty(targetProbe.Events);
        Assert.Empty(otherProbe.Events);
        Assert.Null(pointer.Captured);
        Assert.False(source.IsPressed);

        Assert.True(
            Read<InputResult>(await Call(client, "ui-drag", ("sourceNodeId", sourceId), ("targetNodeId", targetId)))
                .Delivered);
        Assert.Single(sourceProbe.Events, name => name == "PointerPressed");
        Assert.Single(sourceProbe.Events, name => name == "PointerReleased");
        Assert.Null(pointer.Captured);
        Assert.False(source.IsPressed);
    }

    [AvaloniaTheory]
    [InlineData("PointerPressed")]
    [InlineData("PointerMoved")]
    [InlineData("PointerReleased")]
    public async Task Stdio_drag_handler_failure_cleans_capture_and_pressed_state_and_the_next_drag_recovers(
        string failingEvent)
    {
        var source = new Button { Name = "BoundaryThrowingSource", Width = 180, Height = 60 };
        var target = new Border
            { Name = "BoundaryThrowingTarget", Width = 100, Height = 60, Background = Brushes.White };
        using var fixture = new Fixture(source, target);
        await using var agent = fixture.Attach();
        await using var client = await CreateClientAsync(agent.PipeName);
        var sourceId = await Find(client, source.Name!);
        var targetId = await Find(client, target.Name!);
        using var probe = new InputProbe(source);
        var shouldThrow = true;

        void Fail(PointerEventArgs args)
        {
            if (shouldThrow && args.RoutedEvent?.Name == failingEvent &&
                (failingEvent != "PointerMoved" || args.GetCurrentPoint(source).Properties.IsLeftButtonPressed))
            {
                shouldThrow = false;
                throw new InvalidOperationException("Boundary drag handler sentinel");
            }
        }

        source.AddHandler(InputElement.PointerPressedEvent,
            (_, args) => Fail(args),
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        source.AddHandler(InputElement.PointerMovedEvent,
            (_, args) => Fail(args),
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        source.AddHandler(InputElement.PointerReleasedEvent,
            (_, args) => Fail(args),
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        var failure = await Error(client,
            "ui-drag",
            "input_failed",
            ("sourceNodeId", sourceId),
            ("targetNodeId", targetId));
        Assert.Contains("Boundary drag handler sentinel", Text(failure));
        Assert.False(shouldThrow);
        var pointer = Assert.IsAssignableFrom<IPointer>(probe.Pointer);
        Assert.Null(pointer.Captured);
        Assert.False(source.IsPressed);
        var deliveredBeforeUnmatchedRelease = probe.Events.Count;
        await Error(client, "ui-input", "input_state", ("nodeId", sourceId), ("kind", "PointerUp"));
        Assert.Equal(deliveredBeforeUnmatchedRelease, probe.Events.Count);

        probe.Events.Clear();
        Assert.True(
            Read<InputResult>(await Call(client, "ui-drag", ("sourceNodeId", sourceId), ("targetNodeId", targetId)))
                .Delivered);
        Assert.Single(probe.Events, name => name == "PointerPressed");
        Assert.Single(probe.Events, name => name == "PointerReleased");
        Assert.Null(pointer.Captured);
        Assert.False(source.IsPressed);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Headless_stdio_context_menu_action_executes_and_removes_the_real_popup_subtree(bool nested)
    {
        var action = new MenuItem { Name = "BoundaryMenuAction", Header = "Apply", Width = 180, Height = 40 };
        var branch = new MenuItem { Name = "BoundaryMenuBranch", Header = "More", Width = 180, Height = 40 };
        if (nested)
            branch.Items.Add(action);
        var menu = new ContextMenu { Name = "BoundaryContextMenu" };
        menu.Items.Add(nested ? branch : action);
        var anchor = new Button { Name = "BoundaryMenuAnchor", Width = 180, Height = 60, ContextMenu = menu };
        using var fixture = new Fixture(anchor);
        await using var agent = fixture.Attach();
        await using var client = await CreateClientAsync(agent.PipeName);
        var anchorId = await Find(client, anchor.Name!);
        var actions = 0;
        action.Click += (_, _) => actions++;
        try
        {
            Read<InputResult>(await Call(client, "ui-click", ("nodeId", anchorId), ("button", "Right")));
            Assert.True(menu.IsOpen);
            TopLevel.GetTopLevel(menu)!.UpdateLayout();
            if (nested)
            {
                Read<InputResult>(await Call(client, "ui-click", ("nodeId", await Find(client, branch.Name!))));
                Assert.True(branch.IsSubMenuOpen);
                TopLevel.GetTopLevel(action)!.UpdateLayout();
            }

            var actionId = await Find(client, action.Name!);
            var menuId = await Find(client, menu.Name!);
            var openedTree = await Tree(client);
            Assert.Contains(openedTree.Entries, entry => entry.Node.Id == actionId);
            Assert.Contains(openedTree.Entries, entry => entry.Node.Id == menuId);

            Read<InputResult>(await Call(client, "ui-click", ("nodeId", actionId)));
            Assert.Equal(1, actions);
            Assert.False(menu.IsOpen);
            Assert.False(branch.IsSubMenuOpen);
            Assert.Empty(Read<TreeSearchResult>(await Call(client, "search", ("query", action.Name!))).Matches);
            var closedTree = await Tree(client);
            Assert.DoesNotContain(closedTree.Entries, entry => entry.Node.Id == actionId || entry.Node.Id == menuId);
        }
        finally
        {
            menu.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Headless_stdio_popup_and_flyout_actions_close_the_real_popup_tree(bool useFlyout)
    {
        var action = new Button { Name = "BoundaryPopupAction", Width = 160, Height = 60, Content = "Apply" };
        var anchor = new Button { Name = "BoundaryPopupAnchor", Width = 180, Height = 60 };
        using var fixture = new Fixture(false, anchor);
        var popup = new Popup
            { PlacementTarget = anchor, ShouldUseOverlayLayer = false, Child = useFlyout ? null : action };
        var flyout = new Flyout { Content = useFlyout ? action : null };
        var actions = 0;
        anchor.Click += (_, _) =>
        {
            if (useFlyout)
                flyout.ShowAt(anchor);
            else
                popup.Open();
        };
        action.Click += (_, _) =>
        {
            actions++;
            if (useFlyout)
                flyout.Hide();
            else
                popup.Close();
        };
        await using var agent = fixture.Attach();
        await using var client = await CreateClientAsync(agent.PipeName);
        try
        {
            Read<InputResult>(await Call(client, "ui-click", ("nodeId", await Find(client, anchor.Name!))));
            Assert.True(useFlyout ? flyout.IsOpen : popup.IsOpen);
            var popupRoot = Assert.IsType<PopupRoot>(TopLevel.GetTopLevel(action));
            popupRoot.UpdateLayout();
            var actionId = await Find(client, action.Name!);
            var openedTree = await Tree(client);
            Assert.Contains(openedTree.Entries, entry => entry.Node.Id == actionId);
            var popupRootId = Assert.Single(openedTree.Entries,
                entry => entry.ParentId is null && entry.Node.TypeName == typeof(PopupRoot).FullName).Node.Id;

            Assert.True(Read<ControlActionResult>(await Call(client,
                "ui-control-action",
                ("nodeId", actionId),
                ("action", "Invoke"))).Succeeded);
            Assert.Equal(1, actions);
            Assert.False(useFlyout ? flyout.IsOpen : popup.IsOpen);
            Assert.Empty(Read<TreeSearchResult>(await Call(client, "search", ("query", action.Name!))).Matches);
            Assert.DoesNotContain((await Tree(client)).Entries,
                entry => entry.Node.Id == actionId || entry.Node.Id == popupRootId);
        }
        finally
        {
            flyout.Hide();
            popup.Close();
        }
    }

    private static Task<McpClient> CreateClientAsync(string? pipe = null) => McpClient.CreateAsync(
        new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "Agent interaction boundary stdio tests",
            // Use the built CLI and its own runtime configuration, with the actual default profile.
            Arguments = pipe is null ? [CliPath, "mcp"] : [CliPath, "mcp", "--pipe", pipe],
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        }),
        cancellationToken: TestContext.Current.CancellationToken);

    private static ValueTask<CallToolResult> Call(McpClient client,
        string name,
        params (string Name, object? Value)[] values) =>
        client.CallToolAsync(name,
            values.ToDictionary(value => value.Name, value => value.Value),
            cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<CallToolResult> Error(McpClient client,
        string name,
        string code,
        params (string Name, object? Value)[] values)
    {
        var result = await Call(client, name, values);
        Assert.True(result.IsError, Text(result));
        Assert.Contains(code, Text(result));
        return result;
    }

    private static async Task<long> Find(McpClient client, string name) => Assert.Single(
        Read<TreeSearchResult>(await Call(client, "search", ("query", name), ("searchTemplates", true))).Matches,
        item => item.Name == name).Id;

    private static async Task<OfficialTreeSnapshot> Tree(McpClient client) =>
        Read<OfficialTreeSnapshot>(await Call(client, "tree", ("treeKind", "Visual"), ("depth", -1)));

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static T Read<T>(CallToolResult result)
    {
        Assert.False(result.IsError == true, Text(result));
        var root = result.StructuredContent is { } content
            ? JsonNode.Parse(JsonSerializer.Serialize(content))
            : JsonNode.Parse(Text(result));
        if (root is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped))
            root = wrapped;
        return root!.Deserialize<T>(ProtocolJson.Options)!;
    }

    private sealed class InputProbe : IDisposable
    {
        private readonly List<IDisposable> _subscriptions = [];

        public InputProbe(Control target)
        {
            void PointerEvent(object? _, PointerEventArgs args)
            {
                Pointer = args.Pointer;
                Events.Add(args.RoutedEvent!.Name);
            }

            _subscriptions.Add(target.AddDisposableHandler(InputElement.PointerMovedEvent,
                PointerEvent,
                RoutingStrategies.Bubble,
                true));
            _subscriptions.Add(target.AddDisposableHandler(InputElement.PointerPressedEvent,
                PointerEvent,
                RoutingStrategies.Bubble,
                true));
            _subscriptions.Add(target.AddDisposableHandler(InputElement.PointerReleasedEvent,
                PointerEvent,
                RoutingStrategies.Bubble,
                true));
            _subscriptions.Add(target.AddDisposableHandler(InputElement.PointerWheelChangedEvent,
                PointerEvent,
                RoutingStrategies.Bubble,
                true));
            _subscriptions.Add(target.AddDisposableHandler(InputElement.PointerCaptureLostEvent,
                (_, args) => Events.Add(args.RoutedEvent!.Name),
                RoutingStrategies.Bubble,
                true));
            _subscriptions.Add(target.AddDisposableHandler(InputElement.KeyDownEvent,
                (_, args) => Events.Add(args.RoutedEvent!.Name),
                RoutingStrategies.Bubble,
                true));
            _subscriptions.Add(target.AddDisposableHandler(InputElement.KeyUpEvent,
                (_, args) => Events.Add(args.RoutedEvent!.Name),
                RoutingStrategies.Bubble,
                true));
            _subscriptions.Add(target.AddDisposableHandler(InputElement.TextInputEvent,
                (_, args) => Events.Add(args.RoutedEvent!.Name),
                RoutingStrategies.Bubble,
                true));
        }

        public List<string> Events { get; } = [];
        public IPointer? Pointer { get; private set; }

        public void Dispose()
        {
            foreach (var subscription in _subscriptions)
                subscription.Dispose();
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(params Control[] controls) : this(true, controls)
        {
        }

        public Fixture(bool overlayPopups, params Control[] controls)
        {
            var canvas = new Canvas();
            for (var index = 0; index < controls.Length; index++)
            {
                Canvas.SetLeft(controls[index], 25 + index % 3 * 240);
                Canvas.SetTop(controls[index], 25 + index / 3 * 160);
                canvas.Children.Add(controls[index]);
            }

            // A separate Headless implementation can provide real PopupRoots without creating OS windows.
            var implementationType =
                typeof(AvaloniaHeadlessPlatform).Assembly.GetType("Avalonia.Headless.HeadlessWindowImpl", true)!;
            var implementation = (IWindowImpl)Activator.CreateInstance(implementationType,
                new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false, OverlayPopups = overlayPopups })!;
            Window = new Window(implementation) { Width = 780, Height = 520, Content = canvas };
            Window.Show();
            Window.UpdateLayout();
            using var frame = Window.CaptureRenderedFrame();
            Assert.NotNull(frame);
        }

        public Window Window { get; }

        public DiagnosticsAgent Attach(params Window[] otherRoots) => DiagnosticsAgent.Attach(Application.Current!,
            new DiagnosticsAgentOptions
            {
                PipeName = DiagnosticsProtocol.PipePrefix + "agent-boundary-tests-" + Guid.NewGuid().ToString("N"),
                ApplicationName = "Headless agent boundary target", TopLevelProvider = () => [Window, .. otherRoots]
            });

        public void Dispose() => Window.Close();
    }
}
