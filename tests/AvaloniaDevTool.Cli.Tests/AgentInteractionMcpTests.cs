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
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class AgentInteractionMcpTests
{
    private static string CliPath => typeof(AgentInteractionMcpTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == "PreviewCliPath").Value!;

    [Fact]
    public async Task Default_stdio_profile_advertises_independent_interactions_and_native_tools_require_attachment()
    {
        await using var client = await CreateClientAsync();
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        foreach (var name in new[]
                 {
                     "attach-to-app", "tree", "search", "props", "input", "ui-input", "ui-click", "ui-key-chord",
                     "ui-drag",
                     "ui-text-selection", "ui-select-item", "ui-control-action", "ui-wait", "native-windows",
                     "native-tree", "native-action",
                     "file-dialogs", "file-dialog-inspect", "file-dialog-set-paths", "file-dialog-set-filter",
                     "file-dialog-accept",
                     "file-dialog-cancel", "file-dialog-overwrite", "file-dialog-wait"
                 })
            Assert.Contains(tools, tool => tool.Name == name);
        var canonicalInput = Assert.Single(tools, tool => tool.Name == "input").ProtocolTool;
        Assert.Equal(new[] { "Click", "KeyDown", "KeyUp", "Text" },
            canonicalInput.InputSchema.GetProperty("properties")
                .GetProperty("inputEvent").GetProperty("enum").EnumerateArray().Select(item => item.GetString()));
        var expected = OperatingSystem.IsWindows() ? "not_connected" : "unsupported_platform";
        Assert.Contains(expected, Text(await Error(client, "native-windows")));
        Assert.Contains(expected, Text(await Error(client, "file-dialogs")));
        Assert.Contains("not_connected", Text(await Error(client, "ui-click", ("nodeId", 1))));
        foreach (var (name, arguments) in new[]
                 {
                     ("ui-input", new Dictionary<string, object?> { ["nodeId"] = 1, ["kind"] = 0 }),
                     ("ui-drag", new Dictionary<string, object?>
                     {
                         ["sourceNodeId"] = 1, ["targetNodeId"] = 2,
                         ["path"] = new[] { new { x = 10d, y = 10d } }
                     }),
                     ("ui-drag", new Dictionary<string, object?>
                     {
                         ["sourceNodeId"] = 1, ["targetNodeId"] = 2,
                         ["path"] = new[] { new { nodeId = "wrong-domain", x = 10d, y = 10d } }
                     }),
                     ("file-dialog-set-paths",
                         new Dictionary<string, object?> { ["dialogId"] = "unused", ["paths"] = "not-an-array" }),
                     ("native-action", new Dictionary<string, object?> { ["nodeId"] = 1, ["action"] = "Invoke" })
                 })
        {
            var failure = await client.CallToolAsync(name,
                arguments,
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(failure.IsError);
            Assert.Contains("invalid_argument", Text(failure));
        }
    }

    [AvaloniaFact]
    public async Task Stdio_click_delivers_double_tap_and_right_click_opens_a_real_context_menu()
    {
        var menu = new ContextMenu { Items = { new MenuItem { Header = "Inspect" } } };
        var button = new Button
            { Name = "AgentClick", Width = 180, Height = 60, Content = "Click", ContextMenu = menu };
        using var fixture = new Fixture(button);
        await using var agent = fixture.Attach();
        await using var client = await CreateClientAsync(agent.PipeName);
        var id = await Find(client, button.Name!);
        var clicks = 0;
        var doubles = 0;
        var counts = new List<int>();
        button.Click += (_, _) => clicks++;
        button.DoubleTapped += (_, _) => doubles++;
        button.AddHandler(InputElement.PointerPressedEvent,
            (_, e) => counts.Add(e.ClickCount),
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        Assert.True(Read<InputResult>(await Call(client, "ui-click", ("nodeId", id), ("clickCount", 2))).Delivered);
        Assert.Equal(2, clicks);
        Assert.Equal(1, doubles);
        Assert.Equal([1, 2], counts);
        try
        {
            Assert.True(Read<InputResult>(await Call(client,
                "ui-click",
                ("nodeId", id),
                ("button", "Right"),
                ("x", 12),
                ("y", 18))).Delivered);
            fixture.Window.UpdateLayout();
            Assert.True(menu.IsOpen);
            Assert.Equal(2, clicks);
            Assert.False(button.IsPressed);
        }
        finally
        {
            menu.Close();
        }

        Assert.Contains("input_limit", Text(await Error(client, "ui-click", ("nodeId", id), ("clickCount", 4))));
        button.IsEnabled = false;
        Assert.Contains("disabled_target", Text(await Error(client, "ui-click", ("nodeId", id))));
        Assert.Equal(2, clicks);
    }

    [AvaloniaFact]
    public async Task Stdio_chord_and_text_selection_edit_real_text_and_preserve_balanced_modifier_releases()
    {
        var text = new TextBox { Name = "AgentText", Width = 260, Height = 55, Text = "hello world" };
        using var fixture = new Fixture(text);
        await using var agent = fixture.Attach();
        await using var client = await CreateClientAsync(agent.PipeName);
        var id = await Find(client, text.Name!);
        var keys = new List<(bool Pressed, Key Key, KeyModifiers Modifiers)>();
        text.AddHandler(InputElement.KeyDownEvent,
            (_, e) => keys.Add((true, e.Key, e.KeyModifiers)),
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        text.AddHandler(InputElement.KeyUpEvent,
            (_, e) => keys.Add((false, e.Key, e.KeyModifiers)),
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        Assert.True(Read<InputResult>(await Call(client,
            "ui-key-chord",
            ("nodeId", id),
            ("keys", new[] { "A" }),
            ("modifiers", new[] { "Control" }))).Delivered);
        Assert.Equal("hello world", text.SelectedText);
        Assert.Equal(new[]
            {
                (true, Key.LeftCtrl, KeyModifiers.Control), (true, Key.A, KeyModifiers.Control),
                (false, Key.A, KeyModifiers.Control), (false, Key.LeftCtrl, KeyModifiers.None)
            },
            keys);
        var selection =
            Read<TextSelectionResult>(await Call(client,
                "ui-text-selection",
                ("nodeId", id),
                ("start", 6),
                ("length", 5)));
        Assert.Equal("world", selection.SelectedText);
        Assert.Equal(6, selection.SelectionStart);
        Assert.Equal(11, selection.SelectionEnd);
        var replaced = Read<TextSelectionResult>(await Call(client,
            "ui-text-selection",
            ("nodeId", id),
            ("start", 6),
            ("length", 5),
            ("replacementText", "Avalonia")));
        Assert.True(replaced.Replaced);
        Assert.Equal("hello Avalonia", text.Text);
        Assert.Equal(14, replaced.TextLength);
        Read<TextSelectionResult>(await Call(client,
            "ui-text-selection",
            ("nodeId", id),
            ("start", 5),
            ("replacementText", "")));
        Assert.Equal("hello", text.Text);
        Assert.True(Read<InputResult>(await Call(client, "ui-input", ("nodeId", id), ("kind", "Text"), ("text", "!")))
            .Delivered);
        Assert.Equal("hello!", text.Text);
        Assert.Contains("invalid_selection",
            Text(await Error(client, "ui-text-selection", ("nodeId", id), ("start", 99))));
        text.IsReadOnly = true;
        Assert.Contains("readonly_target",
            Text(await Error(client, "ui-text-selection", ("nodeId", id), ("replacementText", "blocked"))));
        Assert.Equal("hello!", text.Text);
    }

    [AvaloniaFact]
    public async Task Stdio_item_and_control_actions_change_real_combo_list_tab_toggle_and_expansion_state()
    {
        var combo = new ComboBox { Name = "AgentCombo", Width = 180, Height = 45, Items = { "One", "Two", "Three" } };
        var list = new ListBox { Name = "AgentList", Width = 180, Height = 120, Items = { "One", "Two", "Three" } };
        var tabs = new TabControl
        {
            Name = "AgentTabs", Width = 200, Height = 140,
            Items =
            {
                new TabItem { Header = "First", Content = "One" }, new TabItem { Header = "Second", Content = "Two" }
            }
        };
        var check = new CheckBox { Name = "AgentToggle", Width = 180, Height = 45, Content = "Option" };
        var button = new Button { Name = "AgentInvoke", Width = 180, Height = 45 };
        using var fixture = new Fixture(combo, list, tabs, check, button);
        await using var agent = fixture.Attach();
        await using var client = await CreateClientAsync(agent.PipeName);
        foreach (var selector in new SelectingItemsControl[] { combo, list, tabs })
        {
            var result = Read<ItemSelectionResult>(await Call(client,
                "ui-select-item",
                ("nodeId", await Find(client, selector.Name!)),
                ("index", 1)));
            Assert.True(result.Succeeded);
            Assert.Equal(1, result.SelectedIndex);
            Assert.Equal(1, selector.SelectedIndex);
            Assert.Same(selector.Items[1], selector.SelectedItem);
            Assert.Equal(selector == tabs ? "Second" : "Two", result.SelectedItem);
        }

        var checkId = await Find(client, check.Name!);
        Assert.Equal("On",
            Read<ControlActionResult>(
                await Call(client, "ui-control-action", ("nodeId", checkId), ("action", "Toggle"))).ToggleState);
        Assert.True(check.IsChecked);
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        var buttonId = await Find(client, button.Name!);
        Assert.True(Read<ControlActionResult>(await Call(client,
            "ui-control-action",
            ("nodeId", buttonId),
            ("action", "Invoke"))).Succeeded);
        Assert.Equal(1, clicks);
        var comboId = await Find(client, combo.Name!);
        try
        {
            Assert.Equal("Expanded",
                Read<ControlActionResult>(await Call(client,
                    "ui-control-action",
                    ("nodeId", comboId),
                    ("action", "Expand"))).ExpandCollapseState);
            Assert.True(combo.IsDropDownOpen);
            Assert.Equal("Collapsed",
                Read<ControlActionResult>(await Call(client,
                    "ui-control-action",
                    ("nodeId", comboId),
                    ("action", "Collapse"))).ExpandCollapseState);
            Assert.False(combo.IsDropDownOpen);
        }
        finally
        {
            combo.IsDropDownOpen = false;
        }

        Assert.Contains("unsupported_action",
            Text(await Error(client, "ui-control-action", ("nodeId", buttonId), ("action", "Expand"))));
        Assert.Contains("invalid_argument",
            Text(await Error(client, "ui-control-action", ("nodeId", buttonId), ("action", "0"))));
    }

    [AvaloniaFact]
    public async Task Stdio_drag_uses_real_slider_capture_and_canonical_waypoints_to_change_value()
    {
        var slider = new Slider
            { Name = "AgentSlider", Width = 300, Height = 60, Minimum = 0, Maximum = 100, Value = 10 };
        using var fixture = new Fixture(slider);
        await using var agent = fixture.Attach();
        await using var client = await CreateClientAsync(agent.PipeName);
        var sliderId = await Find(client, slider.Name!);
        var thumb = Assert.Single(slider.GetVisualDescendants().OfType<Thumb>());
        var thumbId = Assert.Single(Read<TreeSearchResult>(await Call(client,
                "search",
                ("query", "Thumb"),
                ("scopeNodeId", sliderId),
                ("searchTemplates", true))).Matches,
            node => node.TypeName == typeof(Thumb).FullName).Id;
        IPointer? pointer = null;
        var completed = 0;
        thumb.DragCompleted += (_, _) => completed++;
        thumb.AddHandler(InputElement.PointerPressedEvent,
            (_, e) => pointer = e.Pointer,
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        Assert.True(Read<InputResult>(await Call(client,
            "ui-drag",
            ("sourceNodeId", thumbId),
            ("targetNodeId", sliderId),
            ("targetX", 280),
            ("targetY", 30),
            ("path", new[]
            {
                new { nodeId = sliderId, x = 150d, y = 30d }, new { nodeId = sliderId, x = 280d, y = 30d }
            }))).Delivered);

        Assert.True(slider.Value > 85, $"Actual slider value: {slider.Value}");
        Assert.Equal(1, completed);
        Assert.NotNull(pointer);
        Assert.Null(pointer.Captured);
    }

    [AvaloniaFact]
    public async Task Stdio_interactions_reject_expired_public_nodes_after_reattaching_without_mutating_the_target()
    {
        var button = new Button { Name = "AgentEpochButton", Width = 180, Height = 60 };
        var target = new Border { Name = "AgentEpochTarget", Width = 100, Height = 60, Background = Brushes.White };
        using var fixture = new Fixture(button, target);
        await using var agent = fixture.Attach();
        await using var client = await CreateClientAsync(agent.PipeName);
        var oldButton = await Find(client, button.Name!);
        var oldTarget = await Find(client, target.Name!);
        var clicks = 0;
        var presses = 0;
        button.Click += (_, _) => clicks++;
        button.AddHandler(InputElement.PointerPressedEvent,
            (_, _) => presses++,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        Assert.NotEqual(true, (await Call(client, "detach")).IsError);
        var apps = Read<OfficialAttachmentResult>(await Call(client, "attach-to-app"));
        var application = Assert.Single(apps.Applications, app => app.PipeName == agent.PipeName);
        Assert.True(
            Read<OfficialAttachmentResult>(await Call(client, "attach-to-app", ("id", application.Id))).Attached);
        var newButton = await Find(client, button.Name!);
        var newTarget = await Find(client, target.Name!);
        Assert.NotEqual(oldButton, newButton);
        Assert.Contains("stale_node", Text(await Error(client, "ui-click", ("nodeId", oldButton))));
        Assert.Contains("stale_node",
            Text(await Error(client, "ui-drag", ("sourceNodeId", oldButton), ("targetNodeId", newTarget))));
        Assert.Contains("stale_node",
            Text(await Error(client, "ui-drag", ("sourceNodeId", newButton), ("targetNodeId", oldTarget))));
        Assert.Contains("stale_node",
            Text(await Error(client,
                "ui-drag",
                ("sourceNodeId", newButton),
                ("targetNodeId", newTarget),
                ("path", new[] { new { nodeId = oldTarget, x = 10d, y = 10d } }))));
        Assert.Equal(0, clicks);
        Assert.Equal(0, presses);
        Assert.False(button.IsPressed);
        Read<InputResult>(await Call(client, "ui-click", ("nodeId", newButton)));
        Assert.Equal(1, clicks);
    }

    [AvaloniaFact]
    public async Task Stdio_wait_observes_real_properties_allows_other_interactions_and_reports_timeout_and_ambiguity()
    {
        var text = new TextBox { Name = "AgentWaitText", Width = 250, Height = 55, Text = "Before" };
        var duplicateOne = new TextBlock { Name = "AgentWaitDuplicateOne", Width = 180, Height = 40, Text = "First" };
        var duplicateTwo = new TextBlock { Name = "AgentWaitDuplicateTwo", Width = 180, Height = 40, Text = "Second" };
        using var fixture = new Fixture(text, duplicateOne, duplicateTwo);
        await using var agent = fixture.Attach();
        await using var client = await CreateClientAsync(agent.PipeName);
        var id = await Find(client, text.Name!);
        var textProperty = Assert.Single(Read<PropertyInfoDto[]>(await Call(client,
                "props",
                ("nodeId", id),
                ("propertyNames", new[] { "Text" }))),
            property => property.DeclaringType == typeof(TextBlock).FullName).Id;
        var exists = Read<UiWaitResult>(await Call(client, "ui-wait", ("query", text.Name!)));
        Assert.True(exists.Matched);
        Assert.Equal("Exists", exists.Condition);
        Assert.Equal(id, exists.NodeId);
        var absent = Read<UiWaitResult>(await Call(client,
            "ui-wait",
            ("query", "AgentWaitNeverPresent"),
            ("condition", "Absent")));
        Assert.True(absent.Matched);
        Assert.Null(absent.NodeId);
        var equal = Read<UiWaitResult>(await Call(client,
            "ui-wait",
            ("query", text.Name!),
            ("condition", "Equals"),
            ("propertyName", textProperty),
            ("expectedValue", "Before")));
        Assert.True(equal.Matched);
        Assert.Equal("Before", equal.PropertyValue);

        var pending = Call(client,
            "ui-wait",
            ("query", text.Name!),
            ("condition", "Contains"),
            ("propertyName", textProperty),
            ("expectedValue", "Ready"),
            ("timeoutMs", 3000),
            ("pollIntervalMs", 25)).AsTask();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Read<TextSelectionResult>(await Call(client,
            "ui-text-selection",
            ("nodeId", id),
            ("replacementText", "Ready from another stdio call")));
        var contains = Read<UiWaitResult>(await pending);
        Assert.True(contains.Matched);
        Assert.Equal(id, contains.NodeId);
        Assert.Equal("Ready from another stdio call", contains.PropertyValue);
        Assert.Equal(contains.PropertyValue, text.Text);
        Assert.Contains("ui_wait_timeout",
            Text(await Error(client,
                "ui-wait",
                ("query", text.Name!),
                ("condition", "Equals"),
                ("propertyName", textProperty),
                ("expectedValue", "Never matches"),
                ("timeoutMs", 150),
                ("pollIntervalMs", 25))));
        Assert.Contains("ambiguous_query",
            Text(await Error(client,
                "ui-wait",
                ("query", "AgentWaitDuplicate"),
                ("condition", "Equals"),
                ("expectedValue", "First"))));
        Assert.Contains("ambiguous_property",
            Text(await Error(client,
                "ui-wait",
                ("query", text.Name!),
                ("condition", "Equals"),
                ("propertyName", "NoSuchProperty"),
                ("expectedValue", "unused"))));
        Assert.Contains("invalid_argument",
            Text(await Error(client,
                "ui-wait",
                ("query", text.Name!),
                ("condition", "equals"),
                ("expectedValue", text.Text))));
    }

    private static Task<McpClient> CreateClientAsync(string? pipe = null) => McpClient.CreateAsync(
        new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "Independent agent interaction stdio tests",
            // Launch the actual CLI output so its runtime configuration and native worker are present.
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
        params (string Name, object? Value)[] values)
    {
        var result = await Call(client, name, values);
        Assert.True(result.IsError, Text(result));
        return result;
    }

    private static async Task<long> Find(McpClient client, string name) => Assert.Single(
        Read<TreeSearchResult>(await Call(client, "search", ("query", name))).Matches,
        item => item.Name == name).Id;

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

    private sealed class Fixture : IDisposable
    {
        public Fixture(params Control[] controls)
        {
            var canvas = new Canvas();
            for (var index = 0; index < controls.Length; index++)
            {
                Canvas.SetLeft(controls[index], 25 + index % 3 * 220);
                Canvas.SetTop(controls[index], 25 + index / 3 * 180);
                canvas.Children.Add(controls[index]);
            }

            Window = new Window { Width = 720, Height = 500, Content = canvas };
            Window.Show();
            Window.UpdateLayout();
            using var frame = Window.CaptureRenderedFrame();
            Assert.NotNull(frame);
        }

        public Window Window { get; }

        public DiagnosticsAgent Attach() => DiagnosticsAgent.Attach(Application.Current!,
            new DiagnosticsAgentOptions
            {
                PipeName = DiagnosticsProtocol.PipePrefix + "agent-interaction-tests-" + Guid.NewGuid().ToString("N"),
                ApplicationName = "Independent interaction test target", TopLevelProvider = () => [Window]
            });

        public void Dispose() => Window.Close();
    }
}
