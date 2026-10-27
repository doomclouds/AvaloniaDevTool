using System.Runtime.CompilerServices;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class PopupFreezeTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Real_outside_pointer_input_is_suppressed_then_resumes_for_overlay_and_native_popups(bool overlay)
    {
        var anchor = Anchor();
        var window = NativeWindow(anchor);
        window.Show();
        window.UpdateLayout();
        var popup = CreatePopup(anchor, overlay);
        try
        {
            popup.Open();
            window.UpdateLayout();
            Assert.Equal(overlay, popup.IsUsingOverlayLayer);
            OutsideClick(window);
            Assert.False(popup.IsOpen);
            popup.Open();
            window.UpdateLayout();
            var objects = new WeakObjectRegistry();
            using var service = new PopupFreezeService(objects);
            service.RefreshRoots([window]);
            var state = service.Set(new(true, objects.GetId(window)));
            Assert.True(Assert.Single(state.Popups).HasOverride);
            OutsideClick(window);
            Assert.True(popup.IsOpen);
            Assert.False(popup.IsLightDismissEnabled);
            NotifyPlatformFocusLost(window);
            Assert.True(popup.IsOpen);
            service.Resume();
            Assert.True(popup.IsLightDismissEnabled);
            OutsideClick(window);
            Assert.False(popup.IsOpen);
        }
        finally
        {
            popup.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Bound_configuration_remains_live_and_target_detachment_is_not_cancelled()
    {
        var anchor = Anchor();
        var window = NativeWindow(anchor);
        window.Show();
        window.UpdateLayout();
        var popup = CreatePopup(anchor, false);
        var model = new DismissConfiguration();
        using var binding = popup.Bind(Popup.IsLightDismissEnabledProperty,
            new Binding(nameof(DismissConfiguration.Enabled)) { Source = model });
        try
        {
            popup.Open();
            var objects = new WeakObjectRegistry();
            using var service = new PopupFreezeService(objects);
            service.RefreshRoots([window]);
            service.Set(new(true));
            Assert.False(popup.IsLightDismissEnabled);
            model.Enabled = false;
            Assert.Single(service.Status().Conflicts);
            service.Resume();
            Assert.False(popup.IsLightDismissEnabled);
            model.Enabled = true;
            Assert.True(popup.IsLightDismissEnabled);
            service.Set(new(true));
            window.Content = null;
            Assert.False(popup.IsOpen);
            Assert.True(popup.IsLightDismissEnabled);
            Assert.Empty(service.Status().Popups);
        }
        finally
        {
            popup.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Scope_includes_new_nested_popups_but_excludes_other_roots_and_application_close_never_reopens()
    {
        var anchor = Anchor();
        var otherAnchor = Anchor();
        var first = NativeWindow(anchor);
        var second = NativeWindow(otherAnchor);
        first.Show();
        second.Show();
        first.UpdateLayout();
        second.UpdateLayout();
        var innerAnchor = Anchor();
        var popup = CreatePopup(anchor, false);
        popup.Child = innerAnchor;
        var nested = CreatePopup(innerAnchor, false);
        var unrelated = CreatePopup(otherAnchor, true);
        try
        {
            var objects = new WeakObjectRegistry();
            using var service = new PopupFreezeService(objects);
            service.RefreshRoots([first]);
            service.Set(new(true));
            popup.Open();
            first.UpdateLayout();
            Assert.False(popup.IsLightDismissEnabled); // No timer/status refresh is required.
            nested.Open();
            unrelated.Open();
            Assert.False(nested.IsLightDismissEnabled);
            Assert.True(unrelated.IsLightDismissEnabled);
            Assert.Equal(2, service.Status().Popups.Count);
            nested.Close();
            popup.SetCurrentValue(Popup.IsOpenProperty, false);
            Assert.False(nested.IsOpen);
            Assert.False(popup.IsOpen);
            Assert.True(nested.IsLightDismissEnabled);
            Assert.True(popup.IsLightDismissEnabled);
            Assert.Empty(service.Status().Popups);
            service.Resume();
            Assert.False(nested.IsOpen);
            Assert.False(popup.IsOpen);
        }
        finally
        {
            nested.Close();
            popup.Close();
            unrelated.Close();
            first.Close();
            second.Close();
        }
    }

    [AvaloniaFact]
    public void
        Application_configuration_changes_including_same_effective_value_are_preserved_on_resume_and_scope_removal()
    {
        var anchor = Anchor();
        var window = NativeWindow(anchor);
        window.Show();
        window.UpdateLayout();
        var first = CreatePopup(anchor, true);
        var second = CreatePopup(anchor, true);
        var originallyDisabled = CreatePopup(anchor, true);
        originallyDisabled.IsLightDismissEnabled = false;
        try
        {
            first.Open();
            second.Open();
            originallyDisabled.Open();
            var objects = new WeakObjectRegistry();
            using var service = new PopupFreezeService(objects);
            service.RefreshRoots([window]);
            service.Set(new(true));
            first.IsLightDismissEnabled = true;
            second.IsLightDismissEnabled = false; // Replaces SetCurrentValue without changing the effective value.
            Assert.Equal(2, service.Status().Conflicts.Count);
            Assert.All(service.Status().Popups, popup => Assert.False(popup.HasOverride));
            service.Resume();
            Assert.True(first.IsLightDismissEnabled);
            Assert.False(second.IsLightDismissEnabled);
            Assert.False(originallyDisabled.IsLightDismissEnabled);
            service.Set(new(true));
            Assert.False(first.IsLightDismissEnabled);
            service.RefreshRoots([]);
            Assert.True(first.IsLightDismissEnabled);
            Assert.False(second.IsLightDismissEnabled);
            Assert.Empty(service.Status().Roots);
            Assert.Empty(service.Status().Popups);
            Assert.Equal("root_not_found",
                Assert.Throws<RpcException>(() => service.Set(new(true, objects.GetId(window)))).Code);
        }
        finally
        {
            first.Close();
            second.Close();
            originallyDisabled.Close();
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Flyout_freezes_outside_clicks_and_context_menu_reports_its_overlay_interaction_handler_boundary(
        bool overlay)
    {
        var anchor = Anchor();
        var window = NativeWindow(anchor, overlay);
        window.Show();
        window.UpdateLayout();
        var flyout = new Flyout { Content = new Button { Content = "Flyout" } };
        var menu = new ContextMenu { ItemsSource = new[] { new MenuItem { Header = "Menu action" } } };
        anchor.ContextMenu = menu;
        try
        {
            var objects = new WeakObjectRegistry();
            using var service = new PopupFreezeService(objects);
            service.RefreshRoots([window]);
            service.Set(new(true));
            flyout.ShowAt(anchor);
            window.UpdateLayout();
            Assert.Equal(overlay, flyout.Popup.IsUsingOverlayLayer);
            OutsideClick(window);
            Assert.True(flyout.IsOpen);
            Assert.False(flyout.Popup.IsLightDismissEnabled);
            flyout.Hide();
            Assert.False(flyout.IsOpen);
            Assert.False(flyout.Popup.IsOpen);
            Assert.True(flyout.Popup.IsLightDismissEnabled);
            menu.Open(anchor);
            window.UpdateLayout();
            var popup = Assert.Single(window.OpenedPopups);
            Assert.Equal(overlay, popup.IsUsingOverlayLayer);
            Assert.Same(menu, popup.Child);
            Assert.False(popup.IsLightDismissEnabled);
            var menuState = Assert.Single(service.Status().Popups);
            Assert.Equal("ContextMenu", menuState.Kind);
            Assert.Contains("DefaultMenuInteractionHandler", menuState.Limitation);
            OutsideClick(window);
            Assert.Equal(!overlay, menu.IsOpen);
            menu.Close();
            Assert.False(menu.IsOpen);
            Assert.False(popup.IsOpen);
            Assert.True(popup.IsLightDismissEnabled);
            service.Resume();
            Assert.False(flyout.IsOpen);
            Assert.False(menu.IsOpen);
        }
        finally
        {
            flyout.Hide();
            menu.Close();
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Tooltip_service_pointer_dismissal_is_reported_as_limited_and_never_resurrected(bool overlay)
    {
        var anchor = Anchor();
        ToolTip.SetTip(anchor, "Tooltip lifetime");
        ToolTip.SetShowDelay(anchor, 0);
        ToolTip.SetShouldUseOverlayLayer(anchor, overlay);
        var window = NativeWindow(anchor);
        window.Show();
        window.UpdateLayout();
        try
        {
            var objects = new WeakObjectRegistry();
            using var service = new PopupFreezeService(objects);
            service.RefreshRoots([window]);
            service.Set(new(true));
            window.MouseMove(new Point(30, 30));
            window.UpdateLayout();
            Assert.True(ToolTip.GetIsOpen(anchor));
            var popup = Assert.Single(service.Status().Popups);
            Assert.Equal(overlay, popup.IsUsingOverlayLayer);
            Assert.Equal("ToolTip", popup.Kind);
            Assert.False(popup.HasOverride);
            Assert.Contains("ToolTipService", popup.Limitation);
            window.MouseMove(new Point(350, 250));
            Dispatcher.UIThread.RunJobs();
            Assert.False(ToolTip.GetIsOpen(anchor));
            Assert.Empty(service.Status().Popups);
            service.Resume();
            Assert.False(ToolTip.GetIsOpen(anchor));
        }
        finally
        {
            ToolTip.SetIsOpen(anchor, false);
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Frozen_context_menu_keeps_keyboard_escape_and_business_item_click_interactions(bool overlay)
    {
        var anchor = Anchor();
        var window = NativeWindow(anchor, overlay);
        window.Show();
        window.UpdateLayout();
        var item = new MenuItem { Header = "Business action" };
        var menu = new ContextMenu { ItemsSource = new[] { item } };
        anchor.ContextMenu = menu;
        var clicks = 0;
        item.Click += (_, _) => clicks++;
        try
        {
            var objects = new WeakObjectRegistry();
            using var service = new PopupFreezeService(objects);
            service.RefreshRoots([window]);
            service.Set(new(true));
            menu.Open(anchor);
            var menuRoot = TopLevel.GetTopLevel(menu)!;
            menuRoot.UpdateLayout();
            Assert.False(Assert.Single(window.OpenedPopups).IsLightDismissEnabled);
            Assert.True(item.Focus());
            var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            item.RaiseEvent(escape);
            Assert.True(escape.Handled);
            Assert.False(menu.IsOpen);
            Assert.Equal(0, clicks);
            menu.Open(anchor);
            menuRoot = TopLevel.GetTopLevel(menu)!;
            menuRoot.UpdateLayout();
            var position = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), menuRoot)!
                .Value;
            menuRoot.MouseDown(position, MouseButton.Left);
            Assert.True(menu.IsOpen);
            menuRoot.MouseUp(position, MouseButton.Left);
            Assert.Equal(1, clicks);
            Assert.False(menu.IsOpen);
            service.Resume();
            Assert.False(menu.IsOpen);
        }
        finally
        {
            menu.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Named_pipe_disconnect_restores_owned_values_and_never_reopens_closed_popups()
    {
        var anchor = Anchor();
        var window = NativeWindow(anchor);
        window.Show();
        window.UpdateLayout();
        var popup = CreatePopup(anchor, true);
        try
        {
            popup.Open();
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                    { PipeName = "popup-freeze-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            await using var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            var root = Assert.Single((await client.Popups.GetPopupFreezeAsync()).Roots);
            Assert.True((await client.Popups.SetPopupFreezeAsync(new(true, root.RootId))).IsActive);
            Assert.False(popup.IsLightDismissEnabled);
            await client.DisconnectAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!popup.IsLightDismissEnabled)
                await Task.Delay(10, timeout.Token);
            Assert.True(popup.IsOpen);
            popup.Close();
            Assert.False(popup.IsOpen);
        }
        finally
        {
            popup.Close();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Closed_popups_are_not_rooted_by_the_freeze_service()
    {
        var anchor = Anchor();
        var window = NativeWindow(anchor);
        window.Show();
        window.UpdateLayout();
        try
        {
            var objects = new WeakObjectRegistry();
            using var service = new PopupFreezeService(objects);
            service.RefreshRoots([window]);
            service.Set(new(true));
            var reference = OpenAndClosePopup(anchor);
            for (var attempt = 0; attempt < 4 && reference.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            Assert.False(reference.IsAlive);
            Assert.Empty(service.Status().Popups);
            GC.KeepAlive(service);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Closing_the_owner_window_closes_its_popups_and_removes_frozen_scope_without_reopening()
    {
        var anchor = Anchor();
        var window = NativeWindow(anchor);
        window.Show();
        window.UpdateLayout();
        var popup = CreatePopup(anchor, false);
        var objects = new WeakObjectRegistry();
        using var service = new PopupFreezeService(objects);
        service.RefreshRoots([window]);
        service.Set(new(true));
        popup.Open();
        try
        {
            Assert.False(popup.IsLightDismissEnabled);
            window.Close();
            Assert.False(popup.IsOpen);
            Assert.True(popup.IsLightDismissEnabled);
            Assert.Empty(service.Status().Roots);
            Assert.Empty(service.Status().Popups);
            service.RefreshRoots([window]);
            Assert.Empty(service.Status().Roots);
            service.Resume();
            Assert.False(popup.IsOpen);
        }
        finally
        {
            popup.Close();
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Transient_flyout_move_away_is_an_explicit_limit_and_does_not_restore_a_closed_flyout(bool overlay)
    {
        var anchor = Anchor();
        var window = NativeWindow(anchor, overlay);
        window.Show();
        window.UpdateLayout();
        var flyout = new Flyout
        {
            ShowMode = FlyoutShowMode.TransientWithDismissOnPointerMoveAway,
            Content = new Border { Width = 80, Height = 50 }
        };
        try
        {
            var objects = new WeakObjectRegistry();
            using var service = new PopupFreezeService(objects);
            service.RefreshRoots([window]);
            service.Set(new(true));
            flyout.ShowAt(anchor);
            window.UpdateLayout();
            Assert.False(flyout.Popup.IsLightDismissEnabled);
            window.MouseMove(new Point(30, 30));
            Assert.True(flyout.IsOpen);
            window.MouseMove(new Point(350, 250));
            Assert.False(flyout.IsOpen);
            Assert.False(flyout.Popup.IsOpen);
            Assert.True(flyout.Popup.IsLightDismissEnabled);
            service.Resume();
            Assert.False(flyout.IsOpen);
        }
        finally
        {
            flyout.Hide();
            window.Close();
        }
    }

    [AvaloniaFact]
    public void All_root_freeze_follows_new_roots_and_can_resume_one_root_without_affecting_another()
    {
        var firstAnchor = Anchor();
        var secondAnchor = Anchor();
        var first = NativeWindow(firstAnchor);
        var second = NativeWindow(secondAnchor);
        first.Show();
        second.Show();
        first.UpdateLayout();
        second.UpdateLayout();
        var firstPopup = CreatePopup(firstAnchor, true);
        var secondPopup = CreatePopup(secondAnchor, true);
        try
        {
            var objects = new WeakObjectRegistry();
            using var service = new PopupFreezeService(objects);
            service.RefreshRoots([first]);
            service.Set(new(true));
            firstPopup.Open();
            secondPopup.Open();
            Assert.False(firstPopup.IsLightDismissEnabled);
            Assert.True(secondPopup.IsLightDismissEnabled);
            service.RefreshRoots([first, second]);
            Assert.False(secondPopup.IsLightDismissEnabled);
            var state = service.Set(new(false, objects.GetId(first)));
            Assert.False(state.AllRoots);
            Assert.True(firstPopup.IsLightDismissEnabled);
            Assert.False(secondPopup.IsLightDismissEnabled);
            service.Resume();
            Assert.True(firstPopup.IsLightDismissEnabled);
            Assert.True(secondPopup.IsLightDismissEnabled);
        }
        finally
        {
            firstPopup.Close();
            secondPopup.Close();
            first.Close();
            second.Close();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference OpenAndClosePopup(Control anchor)
    {
        var popup = CreatePopup(anchor, true);
        popup.Open();
        popup.Close();
        return new WeakReference(popup);
    }

    private static Button Anchor() => new()
    {
        Content = "Anchor", Width = 80, Height = 30,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(20)
    };

    private static Popup CreatePopup(Control anchor, bool overlay) => new()
    {
        PlacementTarget = anchor, IsLightDismissEnabled = true, ShouldUseOverlayLayer = overlay,
        Child = new Border { Width = 100, Height = 60, Background = Brushes.White }
    };

    private static void OutsideClick(Window window)
    {
        window.UpdateLayout();
        window.MouseDown(new Point(350, 250), MouseButton.Left);
        window.MouseUp(new Point(350, 250), MouseButton.Left);
    }

    private static void NotifyPlatformFocusLost(Window window)
    {
        // Shipping assemblies keep these platform callbacks internal. Invoke the delegates actually wired by Window/Popup.
        var implementation = window.PlatformImpl!;
        var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance;
        var deactivated =
            Assert.IsType<Action>(implementation.GetType().GetProperty("Deactivated", flags)!.GetValue(implementation));
        var lostFocus =
            Assert.IsType<Action>(implementation.GetType().GetProperty("LostFocus", flags)!.GetValue(implementation));
        deactivated();
        lostFocus();
    }

    private static Window NativeWindow(Control content, bool overlayPopups = false)
    {
        var type = typeof(AvaloniaHeadlessPlatform).Assembly.GetType("Avalonia.Headless.HeadlessWindowImpl", true)!;
        var implementation = (IWindowImpl)Activator.CreateInstance(type,
            new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false, OverlayPopups = overlayPopups })!;
        return new Window(implementation) { Content = content, Width = 400, Height = 300, Background = Brushes.White };
    }

    private sealed class DismissConfiguration : INotifyPropertyChanged
    {
        private bool _enabled = true;

        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                PropertyChanged?.Invoke(this, new(nameof(Enabled)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
