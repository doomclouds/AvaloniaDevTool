using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaloniaDevTool.AutomationSample;

public sealed partial class MainWindow : Window
{
    private readonly TextBlock _interactionStatus = Label("InteractionStatus", "Ready");
    private readonly TextBlock _counterPointerTrace = Identify(new TextBlock
    { Text = "[]", IsVisible = false, Width = 0, Height = 0 }, "CounterPointerTrace");
    private readonly TextBlock _doubleTapStatus = Label("DoubleTapStatus", "DoubleTaps=0");
    private readonly Queue<CounterPointerEvent> _counterEvents = new();
    private readonly TextBlock _windowStatus = Label("WindowStatus", "Secondary closed");
    private readonly TextBlock _dialogStatus = Label("DialogStatus", "Dialog not opened");
    private readonly TextBlock _popupStatus = Label("PopupStatus", "Popup closed");
    private readonly TextBlock _flyoutStatus = Label("FlyoutStatus", "Flyout closed");
    private readonly TextBlock _menuStatus = Label("MenuStatus", "Menu not invoked");
    private readonly TextBlock _selectionStatus = Label("SelectionStatus", "Combo=Alpha; List=One; Tab=Controls");
    private readonly TextBlock _scrollStatus = Label("ScrollStatus", "Offset=0");
    private readonly TextBlock _dragStatus = Label("DragStatus", "Drag not started");
    private readonly TextBlock _inputStatus = Label("InputStatus", "Input not committed");
    private Window? _secondary;
    private int _clicks;
    private int _doubleTaps;
    private int _secondaryClicks;
    private string _comboChoice = "Alpha";
    private string _listChoice = "One";
    private string _tabChoice = "Controls";
    private Point? _dragStart;
    private Point _dragOrigin;

    public MainWindow()
    {
        Identify(this, "AutomationMainWindow");
        Title = "AvaloniaDevTool automation acceptance";
        Width = 1120;
        Height = 860;
        MinWidth = 900;
        MinHeight = 660;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = Identify(new DockPanel { LastChildFill = true }, "AutomationRoot");
        root.Children.Add(_counterPointerTrace);
        var menu = CreateMenu();
        DockPanel.SetDock(menu, Dock.Top);
        root.Children.Add(menu);

        var heading = new StackPanel
        {
            Margin = new Thickness(18, 12, 18, 8), Spacing = 6,
            Children =
            {
                Label("SampleHeading", "Real desktop automation acceptance", 22),
                Label("PipeStatus", $"Pipe: {Program.PipeName}"),
                Label("TestRootStatus", $"Test root: {Program.TestRoot ?? "(system picker default)"}"),
                _interactionStatus
            }
        };
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);

        var tabs = Identify(new TabControl { Margin = new Thickness(18, 0, 18, 18) }, "MainTabs");
        tabs.Items.Add(Identify(new TabItem { Header = "Controls", Content = BuildControlsPage() }, "ControlsTab"));
        tabs.Items.Add(Identify(new TabItem { Header = "Files", Content = BuildFilesPage() }, "FilesTab"));
        tabs.Items.Add(Identify(new TabItem { Header = "Scroll and drag", Content = BuildScrollPage() }, "ScrollTab"));
        tabs.SelectionChanged += (_, _) =>
        {
            _tabChoice = (tabs.SelectedItem as TabItem)?.Header?.ToString() ?? "None";
            UpdateSelectionStatus();
        };
        root.Children.Add(tabs);
        Content = root;
        Closed += (_, _) => _secondary?.Close();
    }

    private Menu CreateMenu()
    {
        var menu = Identify(new Menu(), "MainMenu");
        var actions = Identify(new MenuItem { Header = "_Actions" }, "ActionsMenu");
        var invoke = Identify(new MenuItem { Header = "Invoke menu action" }, "MenuActionItem");
        invoke.Click += (_, _) => _menuStatus.Text = "Menu action invoked";
        var check = Identify(new MenuItem { Header = "Checked menu action", ToggleType = MenuItemToggleType.CheckBox }, "MenuCheckItem");
        check.Click += (_, _) => _menuStatus.Text = $"Menu checked={check.IsChecked}";
        var submenu = Identify(new MenuItem { Header = "Nested actions" }, "NestedMenu");
        var nestedAction = Identify(new MenuItem { Header = "Invoke nested action" }, "NestedMenuActionItem");
        nestedAction.Click += (_, _) => _menuStatus.Text = "Nested menu action invoked";
        submenu.Items.Add(nestedAction);
        actions.Items.Add(invoke);
        actions.Items.Add(check);
        actions.Items.Add(submenu);
        menu.Items.Add(actions);
        return menu;
    }

    private Control BuildControlsPage()
    {
        var page = new StackPanel { Spacing = 10, Margin = new Thickness(8, 12) };
        var input = Identify(new TextBox { Text = "initial text", PlaceholderText = "Type an acceptance value", Width = 350 }, "InputTextBox");
        input.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
                _inputStatus.Text = $"Enter: {input.Text}";
        };
        var counter = Identify(new Button { Content = "Increment" }, "CounterButton");
        counter.Click += (_, _) =>
        {
            _interactionStatus.Text = $"Clicks={++_clicks}";
            RecordCounterPointer("click", null, 0, counter);
        };
        counter.AddHandler(PointerPressedEvent, (_, args) => RecordCounterPointer("down", args.ClickCount, args.Timestamp, counter, args),
            RoutingStrategies.Bubble, handledEventsToo: true);
        counter.AddHandler(PointerReleasedEvent, (_, args) => RecordCounterPointer("up", null, args.Timestamp, counter, args),
            RoutingStrategies.Bubble, handledEventsToo: true);
        counter.AddHandler(DoubleTappedEvent, (_, _) => RecordCounterPointer("double", null, 0, counter),
            RoutingStrategies.Bubble, handledEventsToo: true);
        counter.PointerCaptureLost += (_, _) => RecordCounterPointer("capture-lost", null, 0, counter);
        counter.LostFocus += (_, _) => RecordCounterPointer("focus-lost", null, 0, counter);
        AddHandler(PointerPressedEvent, (_, args) => RecordCounterPointer("window-down", args.ClickCount, args.Timestamp, counter, args),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, args) => RecordCounterPointer("window-up", null, args.Timestamp, counter, args),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        var doubleTap = Identify(new Border
        {
            Width = 150, Height = 30, Background = Brushes.LightSteelBlue, Padding = new Thickness(8, 2),
            Child = Label("DoubleTapTargetText", "Double-tap target")
        }, "DoubleTapTarget");
        AutomationProperties.SetAccessibilityView(doubleTap, AccessibilityView.Control);
        doubleTap.DoubleTapped += (_, _) => _doubleTapStatus.Text = $"DoubleTaps={++_doubleTaps}";
        page.Children.Add(Row(input, ActionButton("CommitInputButton", "Commit input", () => _inputStatus.Text = $"Committed: {input.Text}"),
            counter, doubleTap));
        page.Children.Add(Row(_inputStatus, _doubleTapStatus));
        page.Children.Add(BuildManagedGateControls());

        var check = Identify(new CheckBox { Content = "Enable option", IsChecked = false }, "OptionCheckBox");
        check.IsCheckedChanged += (_, _) => _interactionStatus.Text = $"Option={check.IsChecked}";
        var toggle = Identify(new ToggleSwitch { Content = "Toggle option", IsChecked = false }, "OptionToggle");
        toggle.IsCheckedChanged += (_, _) => _interactionStatus.Text = $"Toggle={toggle.IsChecked}";
        var radioA = Identify(new RadioButton { Content = "Mode A", GroupName = "Mode", IsChecked = true }, "ModeARadio");
        var radioB = Identify(new RadioButton { Content = "Mode B", GroupName = "Mode" }, "ModeBRadio");
        radioB.IsCheckedChanged += (_, _) => { if (radioB.IsChecked == true) _interactionStatus.Text = "Mode=B"; };
        radioA.IsCheckedChanged += (_, _) => { if (radioA.IsChecked == true) _interactionStatus.Text = "Mode=A"; };
        page.Children.Add(Row(check, toggle, radioA, radioB));

        var combo = Identify(new ComboBox { Width = 190, SelectedIndex = 0 }, "ChoiceComboBox");
        foreach (var choice in new[] { "Alpha", "Beta", "Gamma" })
            combo.Items.Add(Identify(new ComboBoxItem { Content = choice }, $"Combo{choice}Item"));
        combo.SelectedIndex = 0;
        combo.SelectionChanged += (_, _) =>
        {
            _comboChoice = (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";
            UpdateSelectionStatus();
        };
        var list = Identify(new ListBox { Width = 240, Height = 116, SelectionMode = SelectionMode.Single }, "ChoiceListBox");
        foreach (var choice in new[] { "One", "Two", "Three" })
            list.Items.Add(Identify(new ListBoxItem { Content = choice }, $"List{choice}Item"));
        list.SelectedIndex = 0;
        list.SelectionChanged += (_, _) =>
        {
            _listChoice = (list.SelectedItem as ListBoxItem)?.Content?.ToString() ?? "None";
            UpdateSelectionStatus();
        };
        page.Children.Add(Row(combo, list));
        page.Children.Add(_selectionStatus);

        var slider = Identify(new Slider { Width = 350, Minimum = 0, Maximum = 100, Value = 25 }, "ValueSlider");
        slider.PropertyChanged += (_, args) =>
        {
            if (args.Property == RangeBase.ValueProperty)
                _interactionStatus.Text = $"Slider={slider.Value.ToString("0.0", CultureInfo.InvariantCulture)}";
        };
        page.Children.Add(Row(Label("SliderLabel", "Slider"), slider));

        page.Children.Add(Row(ActionButton("OpenSecondaryButton", "Open secondary window", OpenSecondary),
            ActionButton("OpenDialogButton", "Open modal dialog", async () => await OpenDialogAsync())));
        page.Children.Add(Row(_windowStatus, _dialogStatus));

        var popupButton = ActionButton("OpenPopupButton", "Open popup", () => { });
        var popup = Identify(new Popup { PlacementTarget = popupButton, Placement = PlacementMode.Bottom, IsLightDismissEnabled = true }, "AcceptancePopup");
        popup.Child = new Border
        {
            Padding = new Thickness(14), Background = Brushes.AliceBlue, BorderBrush = Brushes.SteelBlue, BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Label("PopupText", "Popup content is a separate popup root"),
                    ActionButton("PopupActionButton", "Invoke popup action", () => { _popupStatus.Text = "Popup action invoked"; popup.IsOpen = false; })
                }
            }
        };
        popupButton.Click += (_, _) => { popup.IsOpen = true; _popupStatus.Text = "Popup open"; };
        popup.Closed += (_, _) => { if (_popupStatus.Text == "Popup open") _popupStatus.Text = "Popup dismissed"; };

        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    Label("FlyoutText", "Flyout content"),
                    ActionButton("FlyoutActionButton", "Invoke flyout action", () => _flyoutStatus.Text = "Flyout action invoked")
                }
            }
        };
        var flyoutButton = ActionButton("OpenFlyoutButton", "Open flyout", () => { });
        flyoutButton.Click += (_, _) => { _flyoutStatus.Text = "Flyout open"; flyout.ShowAt(flyoutButton); };
        flyout.Closed += (_, _) => { if (_flyoutStatus.Text == "Flyout open") _flyoutStatus.Text = "Flyout dismissed"; };
        var contextTarget = Identify(new Border
        {
            Background = Brushes.Lavender, Padding = new Thickness(10),
            Child = Label("ContextTargetText", "Right-click for context menu")
        }, "ContextMenuTarget");
        var contextMenu = Identify(new ContextMenu(), "AcceptanceContextMenu");
        var contextAction = Identify(new MenuItem { Header = "Invoke context action" }, "ContextMenuActionItem");
        contextAction.Click += (_, _) => _menuStatus.Text = "Context menu action invoked";
        contextMenu.Items.Add(contextAction);
        contextTarget.ContextMenu = contextMenu;
        page.Children.Add(Row(popupButton, flyoutButton, contextTarget));
        page.Children.Add(popup);
        page.Children.Add(Row(_popupStatus, _flyoutStatus));
        page.Children.Add(_menuStatus);
        return new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Control BuildScrollPage()
    {
        var rows = new StackPanel { Spacing = 4 };
        for (var i = 1; i <= 60; i++)
        {
            var number = i;
            rows.Children.Add(ActionButton($"ScrollItem{i:00}", $"Scroll row {i:00}", () => _scrollStatus.Text = $"Clicked row={number:00}"));
        }
        var scroll = Identify(new ScrollViewer
        {
            Height = 240, Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        }, "LongScrollViewer");
        scroll.ScrollChanged += (_, _) => _scrollStatus.Text = $"Offset={scroll.Offset.Y.ToString("0.0", CultureInfo.InvariantCulture)}; Extent={scroll.Extent.Height.ToString("0.0", CultureInfo.InvariantCulture)}";

        var canvas = Identify(new Canvas
        {
            Height = 180, Width = 760, Background = Brushes.Gainsboro, ClipToBounds = true
        }, "DragCanvas");
        var target = Identify(new Border
        {
            Width = 120, Height = 80, Background = Brushes.LightGreen, BorderBrush = Brushes.DarkGreen,
            BorderThickness = new Thickness(2), Child = Label("DropTargetText", "Drop zone"),
            IsHitTestVisible = false
        }, "DropTarget");
        Canvas.SetLeft(target, 600);
        Canvas.SetTop(target, 70);
        canvas.Children.Add(target);
        var handle = Identify(new Border
        {
            Width = 100, Height = 56, Background = Brushes.CornflowerBlue, BorderBrush = Brushes.Navy,
            BorderThickness = new Thickness(2), Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = Label("DragHandleText", "Drag me")
        }, "DragHandle");
        Canvas.SetLeft(handle, 20);
        Canvas.SetTop(handle, 20);
        handle.PointerPressed += (_, args) =>
        {
            if (!args.GetCurrentPoint(handle).Properties.IsLeftButtonPressed) return;
            _dragStart = args.GetPosition(canvas);
            _dragOrigin = new Point(Canvas.GetLeft(handle), Canvas.GetTop(handle));
            args.Pointer.Capture(handle);
            _dragStatus.Text = "Dragging";
            args.Handled = true;
        };
        handle.PointerMoved += (_, args) =>
        {
            if (_dragStart is not { } start) return;
            var delta = args.GetPosition(canvas) - start;
            var x = Math.Clamp(_dragOrigin.X + delta.X, 0, canvas.Bounds.Width - handle.Width);
            var y = Math.Clamp(_dragOrigin.Y + delta.Y, 0, canvas.Bounds.Height - handle.Height);
            Canvas.SetLeft(handle, x);
            Canvas.SetTop(handle, y);
            _dragStatus.Text = $"Dragging x={x:0}; y={y:0}";
        };
        handle.PointerReleased += (_, args) =>
        {
            if (_dragStart is null) return;
            _dragStart = null;
            args.Pointer.Capture(null);
            var center = new Point(Canvas.GetLeft(handle) + handle.Width / 2, Canvas.GetTop(handle) + handle.Height / 2);
            var dropped = new Rect(Canvas.GetLeft(target), Canvas.GetTop(target), target.Width, target.Height).Contains(center);
            _dragStatus.Text = $"Dropped={dropped}; x={Canvas.GetLeft(handle):0}; y={Canvas.GetTop(handle):0}";
            args.Handled = true;
        };
        handle.PointerCaptureLost += (_, _) => _dragStart = null;
        canvas.Children.Add(handle);
        return new StackPanel
        {
            Margin = new Thickness(8, 12), Spacing = 10,
            Children =
            {
                Label("ScrollInstructions", "Scroll to row 60 and invoke it; drag the blue handle into the green drop zone."),
                scroll, _scrollStatus, canvas, _dragStatus,
                ActionButton("ResetDragButton", "Reset drag", () => { Canvas.SetLeft(handle, 20); Canvas.SetTop(handle, 20); _dragStatus.Text = "Drag reset"; })
            }
        };
    }

    private void OpenSecondary()
    {
        if (_secondary is { } existing)
        {
            existing.Activate();
            return;
        }
        var secondaryInput = Identify(new TextBox { Text = "secondary text" }, "SecondaryInput");
        var secondaryStatus = Label("SecondaryStatus", "Secondary ready");
        var window = Identify(new Window
        {
            Title = "Automation secondary window", Width = 460, Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        }, "SecondaryWindow");
        window.Content = new StackPanel
        {
            Margin = new Thickness(22), Spacing = 12,
            Children =
            {
                Label("SecondaryHeading", "Independent desktop window"), secondaryInput,
                ActionButton("SecondaryActionButton", "Apply secondary input", () =>
                {
                    secondaryStatus.Text = $"Applied {++_secondaryClicks}: {secondaryInput.Text}";
                    _windowStatus.Text = secondaryStatus.Text;
                }),
                secondaryStatus,
                ActionButton("CloseSecondaryButton", "Close secondary", () => window.Close())
            }
        };
        window.Closed += (_, _) => { _secondary = null; _windowStatus.Text = "Secondary closed"; };
        _secondary = window;
        window.Show(this);
        _windowStatus.Text = "Secondary open";
    }

    private async Task OpenDialogAsync()
    {
        var input = Identify(new TextBox { Text = "dialog text" }, "DialogInput");
        var dialog = Identify(new Window
        {
            Title = "Automation modal dialog", Width = 440, Height = 230, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        }, "ModalDialog");
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(22), Spacing = 14,
            Children =
            {
                Label("DialogHeading", "The owner is disabled while this dialog is open."), input,
                Row(ActionButton("DialogAcceptButton", "Accept", () => dialog.Close(input.Text)),
                    ActionButton("DialogCancelButton", "Cancel", () => dialog.Close(null)))
            }
        };
        _dialogStatus.Text = "Dialog open";
        var result = await dialog.ShowDialog<string?>(this);
        _dialogStatus.Text = result is null ? "Dialog cancelled" : $"Dialog accepted: {result}";
    }

    private void UpdateSelectionStatus() => _selectionStatus.Text = $"Combo={_comboChoice}; List={_listChoice}; Tab={_tabChoice}";

    private void RecordCounterPointer(string kind, int? clickCount, ulong timestamp, Button counter, PointerEventArgs? args = null)
    {
        var point = args?.GetPosition(counter);
        var source = args?.Source as StyledElement;
        var captured = args?.Pointer.Captured as StyledElement;
        _counterEvents.Enqueue(new CounterPointerEvent(kind, clickCount, timestamp, _clicks, counter.IsPressed,
            source?.Name ?? args?.Source?.GetType().Name, point?.X, point?.Y, captured?.Name ?? captured?.GetType().Name));
        while (_counterEvents.Count > 32) _counterEvents.Dequeue();
        // Property previews are bounded; keep the most recent input evidence first.
        _counterPointerTrace.Text = System.Text.Json.JsonSerializer.Serialize(_counterEvents.Reverse());
    }

    private sealed record CounterPointerEvent(string Kind, int? ClickCount, ulong Timestamp, int Clicks,
        bool IsPressed, string? Source, double? X, double? Y, string? Captured);

    private static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    private static TextBlock Label(string id, string text, double fontSize = 14) =>
        Identify(new TextBlock { Text = text, FontSize = fontSize, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }, id);

    private static Button ActionButton(string id, string text, Action action)
    {
        var button = Identify(new Button { Content = text }, id);
        button.Click += (_, _) => action();
        return button;
    }

    private static T Identify<T>(T control, string id) where T : Control
    {
        control.Name = id;
        AutomationProperties.SetAutomationId(control, id);
        return control;
    }
}
