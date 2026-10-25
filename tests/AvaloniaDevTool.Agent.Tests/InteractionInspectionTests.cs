using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class InteractionInspectionTests
{
    [AvaloniaFact]
    public void Multi_click_delivers_counts_and_double_tapped_gesture()
    {
        var button = new Button { Width = 180, Height = 60, Content = "Twice" };
        using var fixture = new Fixture(button);
        var counts = new List<int>();
        var clicks = 0;
        var doubles = 0;
        button.Click += (_, _) => clicks++;
        button.DoubleTapped += (_, _) => doubles++;
        button.AddHandler(InputElement.PointerPressedEvent, (_, args) => counts.Add(args.ClickCount),
            RoutingStrategies.Bubble, handledEventsToo: true);

        fixture.Service.Click(new(fixture.Id(button), ClickCount: 2));

        Assert.Equal([1, 2], counts);
        Assert.Equal(2, clicks);
        Assert.Equal(1, doubles);
        Assert.False(button.IsPressed);
    }

    [AvaloniaFact]
    public void Right_click_opens_real_context_menu_and_does_not_invoke_left_click()
    {
        var menu = new ContextMenu { Items = { new MenuItem { Header = "Inspect" } } };
        var button = new Button { Width = 180, Height = 60, ContextMenu = menu };
        using var fixture = new Fixture(button);
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        try
        {
            fixture.Service.Click(new(fixture.Id(button), Button: "Right", X: 12, Y: 18));
            fixture.Window.UpdateLayout();
            Assert.True(menu.IsOpen);
            Assert.Equal(0, clicks);
        }
        finally { menu.Close(); }
    }

    [AvaloniaFact]
    public void Chord_selects_all_text_and_pairs_every_modifier_and_key_in_reverse_order()
    {
        var text = new TextBox { Width = 220, Height = 50, Text = "Selection" };
        using var fixture = new Fixture(text);
        var sequence = new List<(bool Pressed, Key Key, KeyModifiers Modifiers)>();
        text.AddHandler(InputElement.KeyDownEvent, (_, e) => sequence.Add((true, e.Key, e.KeyModifiers)),
            RoutingStrategies.Bubble, handledEventsToo: true);
        text.AddHandler(InputElement.KeyUpEvent, (_, e) => sequence.Add((false, e.Key, e.KeyModifiers)),
            RoutingStrategies.Bubble, handledEventsToo: true);

        fixture.Service.Chord(new(fixture.Id(text), ["A"], ["Control"]));

        Assert.Equal("Selection", text.SelectedText);
        Assert.Equal(new[]
        {
            (true, Key.LeftCtrl, KeyModifiers.Control), (true, Key.A, KeyModifiers.Control),
            (false, Key.A, KeyModifiers.Control), (false, Key.LeftCtrl, KeyModifiers.None)
        }, sequence);
        fixture.Service.Send(new(fixture.Id(text), InputKind.Text, Text: "Replaced"));
        Assert.Equal("Replaced", text.Text);
    }

    [AvaloniaFact]
    public void Chord_releases_pressed_keys_even_when_a_handler_throws()
    {
        var button = new Button { Width = 180, Height = 60 };
        using var fixture = new Fixture(button);
        var released = new List<Key>();
        button.KeyDown += (_, e) => { if (e.Key == Key.F2) throw new InvalidOperationException("Handler failed"); };
        button.KeyUp += (_, e) => released.Add(e.Key);

        Assert.Equal("input_failed", Assert.Throws<RpcException>(() =>
            fixture.Service.Chord(new(fixture.Id(button), ["F2"], ["Control", "Shift"]))).Code);

        Assert.Equal([Key.F2, Key.LeftShift, Key.LeftCtrl], released);
    }

    [AvaloniaFact]
    public void Drag_moves_real_slider_thumb_across_controls_and_releases_capture()
    {
        var slider = new Slider { Width = 300, Height = 60, Minimum = 0, Maximum = 100, Value = 10 };
        using var fixture = new Fixture(slider);
        var thumb = Assert.Single(slider.GetVisualDescendants().OfType<Thumb>());
        IPointer? pointer = null;
        var moves = 0;
        var completed = 0;
        thumb.DragCompleted += (_, _) => completed++;
        thumb.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer,
            RoutingStrategies.Bubble, handledEventsToo: true);
        thumb.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(thumb).Properties.IsLeftButtonPressed) moves++;
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        fixture.Service.Drag(new(fixture.Id(thumb), fixture.Id(slider), TargetX: 280, TargetY: 30));

        Assert.True(slider.Value > 85, $"Actual slider value: {slider.Value}");
        Assert.Equal(12, moves);
        Assert.NotNull(pointer);
        Assert.Null(pointer.Captured);
        Assert.Equal(1, completed);
    }

    [AvaloniaFact]
    public void Explicit_pointer_sequence_crosses_controls_with_capture_coordinates_and_respects_cross_root_rejection()
    {
        var source = new Border { Width = 80, Height = 60, Background = Brushes.White };
        var target = new Border { Width = 100, Height = 70, Background = Brushes.White,
            RenderTransform = new ScaleTransform(1.5, 1.25), RenderTransformOrigin = RelativePoint.TopLeft };
        using var fixture = new Fixture(source, target);
        Point? releasePosition = null;
        IPointer? pointer = null;
        source.PointerPressed += (_, e) => pointer = e.Pointer;
        source.PointerReleased += (_, e) => releasePosition = e.GetPosition(fixture.Window);
        var other = new Window { Width = 200, Height = 150, Content = new Border { Background = Brushes.White } };
        other.Show(); other.UpdateLayout();
        try
        {
            fixture.Service.Send(new(fixture.Id(source), InputKind.PointerDown));
            Assert.Equal("cross_root_input", Assert.Throws<RpcException>(() =>
                fixture.Service.Send(new(fixture.Id(other), InputKind.PointerMove))).Code);
            fixture.Service.Send(new(fixture.Id(target), InputKind.PointerMove, X: 20, Y: 25));
            fixture.Service.Send(new(fixture.Id(target), InputKind.PointerUp, X: 20, Y: 25));
            Assert.Equal(target.TranslatePoint(new Point(20, 25), fixture.Window), releasePosition);
            Assert.NotNull(pointer);
            Assert.Null(pointer.Captured);
        }
        finally { other.Close(); }
    }

    [AvaloniaFact]
    public void Drag_path_validates_all_roots_before_pressing_and_maps_explicit_waypoints()
    {
        var source = new Border { Width = 80, Height = 60, Background = Brushes.White };
        var target = new Border { Width = 100, Height = 70, Background = Brushes.White };
        using var fixture = new Fixture(source, target);
        var points = new List<Point>();
        source.PointerMoved += (_, e) => { if (e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) points.Add(e.GetPosition(fixture.Window)); };
        fixture.Service.Drag(new(fixture.Id(source), fixture.Id(target), TargetX: 20, TargetY: 25,
            Path: [new(fixture.Id(source), 12, 15), new(fixture.Id(target), 20, 25)]));
        Assert.Equal(new[] { source.TranslatePoint(new Point(12, 15), fixture.Window)!.Value,
            target.TranslatePoint(new Point(20, 25), fixture.Window)!.Value }, points);
        var presses = 0;
        source.PointerPressed += (_, _) => presses++;
        var other = new Window { Width = 200, Height = 150 };
        other.Show(); other.UpdateLayout();
        try
        {
            Assert.Equal("cross_root_input", Assert.Throws<RpcException>(() => fixture.Service.Drag(new(
                fixture.Id(source), fixture.Id(target), Path: [new(fixture.Id(other), 10, 10)]))).Code);
            Assert.Equal(0, presses);
        }
        finally { other.Close(); }
    }

    [AvaloniaFact]
    public void Text_selection_replaces_and_deletes_through_the_real_editing_pipeline()
    {
        var text = new TextBox { Width = 260, Height = 60, Text = "hello world" };
        using var fixture = new Fixture(text);
        var selected = fixture.Service.SelectText(new(fixture.Id(text), 6, 5));
        Assert.Equal("world", selected.SelectedText);
        Assert.Equal(6, selected.SelectionStart);
        Assert.Equal(11, selected.SelectionEnd);
        fixture.Service.SelectText(new(fixture.Id(text), 6, 5, "Avalonia"));
        Assert.Equal("hello Avalonia", text.Text);
        fixture.Service.SelectText(new(fixture.Id(text), 5, 9, ""));
        Assert.Equal("hello", text.Text);
        fixture.Service.SelectText(new(fixture.Id(text), 2, 0, ""));
        Assert.Equal("hello", text.Text);
        text.IsReadOnly = true;
        Assert.Equal("hello", fixture.Service.SelectText(new(fixture.Id(text))).SelectedText);
        Assert.Equal("readonly_target", Assert.Throws<RpcException>(() => fixture.Service.SelectText(new(fixture.Id(text), ReplacementText: "no"))).Code);
        Assert.Equal("invalid_selection", Assert.Throws<RpcException>(() => fixture.Service.SelectText(new(fixture.Id(text), 2, 10))).Code);
        Assert.Equal("hello", text.Text);
    }

    [AvaloniaFact]
    public void Text_replacement_respects_max_length_and_preserves_the_binding_and_undo_pipeline()
    {
        var model = new TextModel { Value = "Original" };
        var text = new TextBox { Width = 260, Height = 60, MaxLength = 10 };
        text.Bind(TextBox.TextProperty, new Binding(nameof(TextModel.Value)) { Source = model, Mode = BindingMode.TwoWay });
        using var fixture = new Fixture(text);

        var result = fixture.Service.SelectText(new(fixture.Id(text), ReplacementText: "Replacement beyond max length"));

        Assert.Equal("Replacemen", text.Text);
        Assert.Equal(10, result.TextLength);
        Assert.Equal(text.Text, model.Value);
        Assert.True(text.CanUndo);
        text.Undo();
        Assert.Equal("Original", text.Text);
        Assert.Equal("Original", model.Value);
    }

    [AvaloniaFact]
    public void Failed_drag_handler_releases_capture_and_invalid_chord_does_not_deliver_partial_input()
    {
        var source = new Button { Width = 180, Height = 60 };
        var target = new Border { Width = 100, Height = 70, Background = Brushes.White };
        using var fixture = new Fixture(source, target);
        IPointer? pointer = null;
        source.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer,
            RoutingStrategies.Bubble, handledEventsToo: true);
        source.PointerMoved += (_, e) =>
        {
            if (e.GetCurrentPoint(source).Properties.IsLeftButtonPressed) throw new InvalidOperationException("Move failed");
        };
        Assert.Equal("input_failed", Assert.Throws<RpcException>(() =>
            fixture.Service.Drag(new(fixture.Id(source), fixture.Id(target)))).Code);
        Assert.NotNull(pointer);
        Assert.Null(pointer.Captured);
        Assert.False(source.IsPressed);
        var keyCount = 0;
        source.KeyDown += (_, _) => keyCount++;
        Assert.Equal("invalid_input", Assert.Throws<RpcException>(() =>
            fixture.Service.Chord(new(fixture.Id(source), ["A", "NotAKey"], ["Control"]))).Code);
        Assert.Equal(0, keyCount);
    }

    [AvaloniaFact]
    public void Index_selection_updates_real_combo_list_and_tab_state_and_reports_actual_selected_item()
    {
        var combo = new ComboBox { Width = 180, Height = 45, Items = { "One", "Two", "Three" } };
        var list = new ListBox { Width = 180, Height = 120, Items = { "One", "Two", "Three" } };
        var tabs = new TabControl { Width = 200, Height = 150,
            Items = { new TabItem { Header = "First", Content = "One" }, new TabItem { Header = "Second", Content = "Two" } } };
        using var fixture = new Fixture(combo, list, tabs);
        foreach (var selector in new SelectingItemsControl[] { combo, list, tabs })
        {
            var result = fixture.Service.SelectItem(new(fixture.Id(selector), 1));
            Assert.True(result.Succeeded);
            Assert.Equal(1, result.SelectedIndex);
            Assert.Equal(1, selector.SelectedIndex);
            Assert.Same(selector.Items[1], selector.SelectedItem);
        }
        Assert.Equal("Second", fixture.Service.SelectItem(new(fixture.Id(tabs), 1)).SelectedItem);
        Assert.Equal("invalid_selection", Assert.Throws<RpcException>(() => fixture.Service.SelectItem(new(fixture.Id(list), 99))).Code);
        Assert.Equal(1, list.SelectedIndex);
    }

    [AvaloniaFact]
    public void Automation_actions_invoke_toggle_and_expand_collapse_real_controls()
    {
        var button = new Button { Width = 180, Height = 45 };
        var check = new CheckBox { Width = 180, Height = 45, Content = "Option" };
        var combo = new ComboBox { Width = 180, Height = 45, Items = { "First", "Second" } };
        using var fixture = new Fixture(button, check, combo);
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        fixture.Service.PerformAction(new(fixture.Id(button), ControlAction.Invoke));
        Assert.Equal(1, clicks);
        Assert.Equal("On", fixture.Service.PerformAction(new(fixture.Id(check), ControlAction.Toggle)).ToggleState);
        Assert.True(check.IsChecked);
        Assert.Equal("Off", fixture.Service.PerformAction(new(fixture.Id(check), ControlAction.Toggle)).ToggleState);
        Assert.False(check.IsChecked);
        try
        {
            Assert.Equal("Expanded", fixture.Service.PerformAction(new(fixture.Id(combo), ControlAction.Expand)).ExpandCollapseState);
            Assert.True(combo.IsDropDownOpen);
            Assert.Equal("Collapsed", fixture.Service.PerformAction(new(fixture.Id(combo), ControlAction.Collapse)).ExpandCollapseState);
            Assert.False(combo.IsDropDownOpen);
            Assert.Equal("unsupported_action", Assert.Throws<RpcException>(() => fixture.Service.PerformAction(new(fixture.Id(button), ControlAction.Expand))).Code);
        }
        finally { combo.IsDropDownOpen = false; }
    }

    [AvaloniaFact]
    public async Task Public_client_methods_round_trip_new_contracts_and_change_real_control_state()
    {
        var text = new TextBox { Name = "InteractionText", Width = 180, Height = 45, Text = "Before" };
        var button = new Button { Name = "InteractionButton", Width = 180, Height = 45 };
        var check = new CheckBox { Name = "InteractionCheck", Width = 180, Height = 45 };
        var list = new ListBox { Name = "InteractionList", Width = 180, Height = 100, Items = { "First", "Second" } };
        var source = new Border { Name = "InteractionDragSource", Width = 80, Height = 45, Background = Brushes.White };
        var target = new Border { Name = "InteractionDragTarget", Width = 80, Height = 45, Background = Brushes.White };
        using var fixture = new Fixture(text, button, check, list, source, target);
        await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
        { PipeName = "interaction-tests-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [fixture.Window] });
        await using var client = new DiagnosticsClient();
        await client.ConnectAsync(agent.PipeName);
        async Task<long> Id(string name) => Assert.Single((await client.SearchTreeAsync(new(name, TreeKind.Visual))).Matches).Id;
        var textId = await Id(text.Name!);
        await client.SendKeyChordAsync(new(textId, ["A"], ["Control"]));
        Assert.Equal("Before", text.SelectedText);
        var selection = await client.SelectTextAsync(new(textId, ReplacementText: "After"));
        Assert.True(selection.Replaced);
        Assert.Equal("After", text.Text);
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        var buttonId = await Id(button.Name!);
        await client.ClickAsync(new(buttonId, 2));
        await client.PerformControlActionAsync(new(buttonId, ControlAction.Invoke));
        Assert.Equal(3, clicks);
        Assert.Equal("On", (await client.PerformControlActionAsync(new(await Id(check.Name!), ControlAction.Toggle))).ToggleState);
        Assert.True(check.IsChecked);
        Assert.Equal(1, (await client.SelectItemAsync(new(await Id(list.Name!), 1))).SelectedIndex);
        Assert.Equal("Second", list.SelectedItem);
        Point? release = null;
        source.PointerReleased += (_, e) => release = e.GetPosition(fixture.Window);
        await client.DragAsync(new(await Id(source.Name!), await Id(target.Name!), TargetX: 20, TargetY: 25));
        Assert.Equal(target.TranslatePoint(new Point(20, 25), fixture.Window), release);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly WeakObjectRegistry _registry = new();
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
            Window.Show(); Window.UpdateLayout();
            using var frame = Window.CaptureRenderedFrame();
            Service = new InputInspectionService(_registry);
        }
        public Window Window { get; }
        public InputInspectionService Service { get; }
        public long Id(Control control) => _registry.GetId(control);
        public void Dispose() { Service.Dispose(); Window.Close(); }
    }

    private sealed class TextModel : System.ComponentModel.INotifyPropertyChanged
    {
        private string? _value;
        public string? Value
        {
            get => _value;
            set { _value = value; PropertyChanged?.Invoke(this, new(nameof(Value))); }
        }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
