using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class PopupRootDiscoveryTests
{
    [AvaloniaFact]
    public async Task Native_flyout_roots_follow_focus_and_are_removed_without_observing_unrelated_windows()
    {
        var first = new TextBox { Name = "FlyoutInput" };
        var second = new CheckBox { Name = "FlyoutCheckbox", Content = "Option" };
        var nestedFlyout = new Flyout { Content = new TextBox { Name = "NestedFlyoutInput" } };
        var nestedAnchor = new Button { Content = "Nested", Flyout = nestedFlyout };
        var flyout = new Flyout { Content = new StackPanel { Children = { first, second, nestedAnchor } } };
        var button = new Button { Name = "FlyoutButton", Content = "Open", Flyout = flyout };
        var window = NativePopupWindow();
        window.Name = "ScopedWindow";
        window.Content = button;
        window.Width = 400;
        window.Height = 300;
        var unrelatedAnchor = new Button();
        var unrelated = NativePopupWindow();
        unrelated.Name = "UnrelatedWindow";
        unrelated.Content = unrelatedAnchor;
        var unrelatedPopup = new Popup
        {
            PlacementTarget = unrelatedAnchor, Child = new TextBox { Name = "UnrelatedPopupInput" },
            IsLightDismissEnabled = false, ShouldUseOverlayLayer = false
        };
        window.Show();
        unrelated.Show();
        window.UpdateLayout();
        unrelated.UpdateLayout();
        try
        {
            unrelatedPopup.Open();
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                    { PipeName = "flyout-roots-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            await using var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            Assert.Single(await client.GetRootsAsync(TreeKind.Visual));
            button.Focus();
            await client.SetFollowFocusAsync(true);
            flyout.Popup.ShouldUseOverlayLayer = false;
            flyout.ShowAt(button);
            window.UpdateLayout();
            var popupRoot = Assert.IsType<PopupRoot>(TopLevel.GetTopLevel(first));
            Assert.DoesNotContain(flyout.Popup, window.GetLogicalDescendants().OfType<Popup>());
            Assert.Contains(flyout.Popup, window.OpenedPopups);
            var roots = await client.GetRootsAsync(TreeKind.Visual);
            Assert.Equal(2, roots.Count);
            Assert.DoesNotContain(roots, root => root.Name == "UnrelatedWindow");
            Assert.Empty((await client.SearchTreeAsync(new("UnrelatedPopupInput", TreeKind.Visual))).Matches);
            var checkbox = (await client.SearchTreeAsync(new("FlyoutCheckbox", TreeKind.Visual))).Matches.Single();
            var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.SelectionChanged += change =>
            {
                if (change.ObjectId == checkbox.Id)
                    selected.TrySetResult();
            };
            Assert.True(second.Focus());
            await selected.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await client.HighlightAsync(checkbox.Id);
            var overlay = await client.GetOverlaySnapshotAsync();
            Assert.True(overlay.IsVisible);
            Assert.Equal(checkbox.Id, overlay.ObjectId);
            var path = await client.GetAncestorsAsync(checkbox.Id, TreeKind.Visual);
            Assert.Contains(roots, root => root.Id == path[0].Id);
            await client.GetChildrenAsync(path[0].Id, TreeKind.Visual);
            nestedFlyout.ShowAt(nestedAnchor);
            Assert.Contains(nestedFlyout.Popup, flyout.Popup.OpenedPopups);
            Assert.Equal(3, (await client.GetRootsAsync(TreeKind.Visual)).Count);
            Assert.Single((await client.SearchTreeAsync(new("NestedFlyoutInput", TreeKind.Visual))).Matches);
            nestedFlyout.Hide();
            Assert.Equal(2, (await client.GetRootsAsync(TreeKind.Visual)).Count);
            flyout.Hide();
            Assert.Single(await client.GetRootsAsync(TreeKind.Visual));
            Assert.Equal(0, (await client.ReleaseTreeAsync([], TreeKind.Visual)).ObservedBranches);
            GC.KeepAlive(popupRoot);
        }
        finally
        {
            nestedFlyout.Hide();
            flyout.Hide();
            unrelatedPopup.Close();
            window.Close();
            unrelated.Close();
        }
    }

    private static Window NativePopupWindow()
    {
        // The default headless platform forces overlay popups, hiding native PopupRoot regressions.
        // A separate real HeadlessWindowImpl keeps this test's setting local to its own windows.
        var type = typeof(AvaloniaHeadlessPlatform).Assembly.GetType("Avalonia.Headless.HeadlessWindowImpl",
            throwOnError: true)!;
        var implementation = (IWindowImpl)Activator.CreateInstance(type,
            new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false, OverlayPopups = false })!;
        return new Window(implementation);
    }
}
