using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class InputInspectionTests
{
    [AvaloniaFact]
    public void Text_and_key_events_use_real_textbox_editing_and_modifier_behavior()
    {
        var text = new TextBox { Width = 220, Height = 50 };
        using var fixture = new Fixture(text);
        object? source = null;
        text.AddHandler(InputElement.TextInputEvent, (_, args) => source = args.Source,
            RoutingStrategies.Bubble, handledEventsToo: true);

        var typed = fixture.Send(InputKind.Text, text: "abc");
        Assert.True(typed.Delivered);
        Assert.True(typed.Handled);
        Assert.Same(text, source);
        Assert.Equal("abc", text.Text);
        var back = fixture.Send(InputKind.KeyDown, key: "Back");
        Assert.True(back.Delivered);
        Assert.Equal("ab", text.Text);
        Assert.True(fixture.Send(InputKind.KeyUp, key: "Back").Delivered);
        fixture.Send(InputKind.KeyDown, key: "A", modifiers: ["Control"]);
        fixture.Send(InputKind.Text, text: "替换");
        Assert.Equal("替换", text.Text);
    }

    [AvaloniaFact]
    public void Key_events_expose_exact_source_key_and_modifiers()
    {
        var text = new TextBox { Width = 220, Height = 50 };
        using var fixture = new Fixture(text);
        KeyEventArgs? observed = null;
        text.AddHandler(InputElement.KeyUpEvent, (_, args) => observed = args,
            RoutingStrategies.Bubble, handledEventsToo: true);

        var result = fixture.Send(InputKind.KeyUp, key: "F2", modifiers: ["Control", "Shift"]);
        Assert.True(result.Delivered);
        Assert.NotNull(observed);
        Assert.Same(text, observed.Source);
        Assert.Equal(Key.F2, observed.Key);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, observed.KeyModifiers);
        Assert.Equal(KeyDeviceType.Keyboard, observed.KeyDeviceType);
    }

    [AvaloniaFact]
    public void Click_raises_pointer_events_and_exactly_one_business_click()
    {
        var button = new Button { Width = 180, Height = 64, Content = "Click" };
        using var fixture = new Fixture(button);
        var clicks = 0;
        var presses = 0;
        var releases = 0;
        IPointer? pointer = null;
        button.Click += (_, _) => clicks++;
        button.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
        {
            presses++;
            pointer = args.Pointer;
            Assert.Same(button, args.Source);
            Assert.True(args.GetCurrentPoint(button).Properties.IsLeftButtonPressed);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        button.AddHandler(InputElement.PointerReleasedEvent, (_, args) =>
        {
            releases++;
            Assert.Same(pointer, args.Pointer);
            Assert.False(args.GetCurrentPoint(button).Properties.IsLeftButtonPressed);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        var result = fixture.Send(InputKind.Click);
        Assert.True(result.Delivered);
        Assert.True(result.Handled);
        Assert.Equal(1, presses);
        Assert.Equal(1, releases);
        Assert.Equal(1, clicks);
        Assert.NotNull(pointer);
        Assert.Null(pointer.Captured);
        Assert.False(button.IsPressed);
    }

    [AvaloniaFact]
    public void Pointer_pairing_and_dispose_release_only_session_capture()
    {
        var button = new Button { Width = 180, Height = 64, Content = "Press" };
        using var fixture = new Fixture(button);
        IPointer? pointer = null;
        button.AddHandler(InputElement.PointerPressedEvent, (_, args) => pointer = args.Pointer,
            RoutingStrategies.Bubble, handledEventsToo: true);

        fixture.Send(InputKind.PointerDown);
        Assert.True(button.IsPressed);
        Assert.NotNull(pointer);
        Assert.Same(button, pointer.Captured);
        Assert.Equal("input_state", Assert.Throws<RpcException>(() => fixture.Send(InputKind.PointerDown)).Code);
        Assert.Equal("input_state", Assert.Throws<RpcException>(() => fixture.Send(InputKind.PointerUp, button: "Right")).Code);
        fixture.Service.Dispose();
        Assert.Null(pointer.Captured);
        Assert.False(button.IsPressed);
        Assert.Throws<ObjectDisposedException>(() => fixture.Send(InputKind.PointerMove));
    }

    [AvaloniaFact]
    public void Explicit_down_and_up_share_pointer_and_finish_the_sequence()
    {
        var button = new Button { Width = 180, Height = 64, Content = "Pair" };
        using var fixture = new Fixture(button);
        IPointer? downPointer = null;
        IPointer? upPointer = null;
        var clicks = 0;
        button.Click += (_, _) => clicks++;
        button.AddHandler(InputElement.PointerPressedEvent, (_, args) => downPointer = args.Pointer,
            RoutingStrategies.Bubble, handledEventsToo: true);
        button.AddHandler(InputElement.PointerReleasedEvent, (_, args) => upPointer = args.Pointer,
            RoutingStrategies.Bubble, handledEventsToo: true);

        fixture.Send(InputKind.PointerDown);
        fixture.Send(InputKind.PointerMove, x: 15, y: 20);
        fixture.Send(InputKind.PointerUp);
        Assert.Same(downPointer, upPointer);
        Assert.Null(upPointer!.Captured);
        Assert.Equal(1, clicks);
        Assert.Equal("input_state", Assert.Throws<RpcException>(() => fixture.Send(InputKind.PointerUp)).Code);
    }

    [AvaloniaFact]
    public void Pointer_and_wheel_preserve_local_coordinates_after_real_visual_transform()
    {
        var target = new Border
        {
            Width = 120, Height = 80, Background = Brushes.White,
            RenderTransformOrigin = RelativePoint.TopLeft,
            RenderTransform = new ScaleTransform(1.5, 2)
        };
        using var fixture = new Fixture(target);
        Point? movedLocal = null;
        Point? movedRoot = null;
        PointerWheelEventArgs? wheel = null;
        target.PointerMoved += (_, args) =>
        {
            Assert.Same(target, args.Source);
            movedLocal = args.GetPosition(target);
            movedRoot = args.GetPosition(fixture.Window);
        };
        target.PointerWheelChanged += (_, args) => { wheel = args; args.Handled = true; };

        var expectedRoot = target.TranslatePoint(new Point(12, 18), fixture.Window);
        var moved = fixture.Send(InputKind.PointerMove, x: 12, y: 18, modifiers: ["Alt"]);
        Assert.True(moved.Delivered);
        Assert.NotNull(movedLocal);
        Assert.NotNull(movedRoot);
        Assert.NotNull(expectedRoot);
        Assert.Equal(12, movedLocal.Value.X, 8);
        Assert.Equal(18, movedLocal.Value.Y, 8);
        Assert.Equal(expectedRoot.Value.X, movedRoot.Value.X, 8);
        Assert.Equal(expectedRoot.Value.Y, movedRoot.Value.Y, 8);

        var result = fixture.Service.Send(new InputRequest(fixture.TargetId, InputKind.Wheel,
            Modifiers: ["Shift"], X: 12, Y: 18, DeltaX: 1, DeltaY: -2));
        Assert.True(result.Handled);
        Assert.NotNull(wheel);
        Assert.Same(target, wheel.Source);
        Assert.Equal(12, wheel.GetPosition(target).X, 8);
        Assert.Equal(18, wheel.GetPosition(target).Y, 8);
        Assert.Equal(new Vector(1, -2), wheel.Delta);
        Assert.Equal(KeyModifiers.Shift, wheel.KeyModifiers);
    }

    [AvaloniaFact]
    public void Session_pointer_does_not_replace_headless_device_capture()
    {
        var target = new Border { Width = 120, Height = 80, Background = Brushes.White };
        using var fixture = new Fixture(target);
        IPointer? devicePointer = null;
        IPointer? sessionPointer = null;
        target.PointerMoved += (_, args) =>
        {
            if (devicePointer is null) devicePointer = args.Pointer;
            else sessionPointer = args.Pointer;
        };
        var point = target.TranslatePoint(new Point(10, 10), fixture.Window)!.Value;
        fixture.Window.MouseMove(point);
        Assert.NotNull(devicePointer);
        devicePointer.Capture(target);
        try
        {
            fixture.Send(InputKind.PointerMove, x: 10, y: 10);
            Assert.NotNull(sessionPointer);
            Assert.NotSame(devicePointer, sessionPointer);
            fixture.Service.Dispose();
            Assert.Same(target, devicePointer.Captured);
        }
        finally { devicePointer.Capture(null); }
    }

    [AvaloniaFact]
    public void Readonly_text_is_rejected_and_disabled_or_hidden_ancestors_block_input()
    {
        var text = new TextBox { Width = 220, Height = 50, Text = "Original", IsReadOnly = true };
        using var fixture = new Fixture(text);
        Assert.Equal("readonly_target", Assert.Throws<RpcException>(() => fixture.Send(InputKind.Text, text: "No")).Code);
        fixture.Send(InputKind.KeyDown, key: "Back");
        Assert.Equal("Original", text.Text);

        fixture.Host.IsEnabled = false;
        Assert.Equal("disabled_target", Assert.Throws<RpcException>(() => fixture.Send(InputKind.Click)).Code);
        fixture.Host.IsEnabled = true;
        fixture.Host.IsVisible = false;
        Assert.Equal("hidden_target", Assert.Throws<RpcException>(() => fixture.Send(InputKind.PointerMove)).Code);
    }

    [AvaloniaFact]
    public void Invalid_names_text_limits_and_coordinates_fail_explicitly()
    {
        var text = new TextBox { Width = 220, Height = 50 };
        using var fixture = new Fixture(text);
        foreach (var key in new[] { "1", "Control+A", "DefinitelyNotAKey", "None" })
            Assert.Equal("invalid_input", Assert.Throws<RpcException>(() => fixture.Send(InputKind.KeyDown, key: key)).Code);
        Assert.Equal("invalid_input", Assert.Throws<RpcException>(() => fixture.Send(InputKind.Text)).Code);
        Assert.Equal("input_limit", Assert.Throws<RpcException>(() =>
            fixture.Send(InputKind.Text, text: new string('a', 2049))).Code);
        Assert.Equal("invalid_input", Assert.Throws<RpcException>(() => fixture.Send(InputKind.Click, button: "0")).Code);
        Assert.Equal("invalid_input", Assert.Throws<RpcException>(() => fixture.Send(InputKind.Click, button: "None")).Code);
        Assert.Equal("invalid_input", Assert.Throws<RpcException>(() => fixture.Send(InputKind.Click, modifiers: ["Ctrl"])).Code);
        Assert.Equal("invalid_input", Assert.Throws<RpcException>(() => fixture.Send(InputKind.Click, modifiers: ["None", "Shift"])).Code);
        Assert.Equal("invalid_input", Assert.Throws<RpcException>(() => fixture.Send(InputKind.Click, modifiers: ["Shift", "shift"])).Code);
        foreach (var x in new[] { double.NaN, double.PositiveInfinity, -1, 221 })
            Assert.Equal("invalid_coordinates", Assert.Throws<RpcException>(() => fixture.Send(InputKind.PointerMove, x: x)).Code);
        Assert.Equal("invalid_coordinates", Assert.Throws<RpcException>(() => fixture.Service.Send(
            new InputRequest(fixture.TargetId, InputKind.Wheel, DeltaY: double.NaN))).Code);
        Assert.Equal("unsupported_input", Assert.Throws<RpcException>(() => fixture.Send((InputKind)99)).Code);
        Assert.True(fixture.Send(InputKind.Text, text: new string('a', 2048)).Delivered);
        Assert.Equal(2048, text.Text!.Length);
    }

    [AvaloniaFact]
    public void Expired_detached_zero_sized_and_nonfocusable_targets_do_not_report_success()
    {
        var target = new Border { Width = 120, Height = 80, Background = Brushes.White };
        using var fixture = new Fixture(target);
        Assert.Equal("object_expired", Assert.Throws<RpcException>(() =>
            fixture.Service.Send(new InputRequest(long.MaxValue, InputKind.Click))).Code);
        Assert.Equal("focus_failed", Assert.Throws<RpcException>(() => fixture.Send(InputKind.KeyDown, key: "A")).Code);
        fixture.Host.Children.Remove(target);
        Assert.Equal("detached_object", Assert.Throws<RpcException>(() => fixture.Send(InputKind.Click)).Code);
        fixture.Host.Children.Add(target);
        target.Width = 0;
        fixture.Window.UpdateLayout();
        Assert.Equal("invalid_layout", Assert.Throws<RpcException>(() => fixture.Send(InputKind.Click)).Code);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(Control target)
        {
            Target = target;
            Registry = new WeakObjectRegistry();
            TargetId = Registry.GetId(target);
            Host = new Canvas { Children = { target } };
            Canvas.SetLeft(target, 35);
            Canvas.SetTop(target, 45);
            Window = new Window { Width = 600, Height = 450, Content = Host };
            Window.Show();
            Window.UpdateLayout();
            using var frame = Window.CaptureRenderedFrame();
            Service = new InputInspectionService(Registry);
        }

        public Control Target { get; }
        public WeakObjectRegistry Registry { get; }
        public long TargetId { get; }
        public Canvas Host { get; }
        public Window Window { get; }
        public InputInspectionService Service { get; }

        public InputResult Send(InputKind kind, string? text = null, string? key = null,
            IReadOnlyList<string>? modifiers = null, double? x = null, double? y = null, string button = "Left") =>
            Service.Send(new InputRequest(TargetId, kind, text, key, modifiers, x, y, Button: button));

        public void Dispose()
        {
            Service.Dispose();
            Window.Close();
        }
    }
}

