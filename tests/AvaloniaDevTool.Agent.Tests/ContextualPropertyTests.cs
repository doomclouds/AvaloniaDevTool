using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class ContextualPropertyTests
{
    [AvaloniaFact]
    public async Task Real_layout_parents_filter_only_known_properties_and_preserve_legacy_and_unknown_members()
    {
        var gridChild = new Border { Name = "GridChild" };
        var canvasChild = new Border { Name = "CanvasChild" };
        var dockChild = new Border { Name = "DockChild" };
        var relativeChild = new Border { Name = "RelativeChild" };
        var plainChild = new Border { Name = "PlainChild" };
        Grid.SetRow(plainChild, 3); Canvas.SetLeft(plainChild, 42); DockPanel.SetDock(plainChild, Dock.Bottom);
        RelativePanel.SetAlignLeftWithPanel(plainChild, true);
        plainChild.SetValue(CustomOwner.TagProperty, 7);
        Grid.SetIsSharedSizeScope(plainChild, true); ToolTip.SetTip(plainChild, "Still applicable");
        var root = new StackPanel { Children =
        {
            new Grid { Children = { gridChild } }, new Canvas { Children = { canvasChild } },
            new DockPanel { Children = { dockChild } }, new RelativePanel { Children = { relativeChild } }, plainChild
        } };
        var window = new Window { Content = root, Width = 500, Height = 400 }; window.Show();
        try
        {
            await using var scope = await Scope.Create(window);
            var id = await scope.Find("PlainChild");
            var legacy = await scope.Client.GetPropertiesAsync(id);
            Assert.Contains(legacy, row => row.Id == "Avalonia.Controls.Grid:Row" && row.Value == "3");
            Assert.False(Assert.Single(legacy, row => row.Id == "Avalonia.Controls.Grid:Row").Presentation!.Context!.IsApplicable);
            var contextual = await scope.Client.GetPropertiesAsync(new PropertiesRequest(id, Contextual: true));
            Assert.DoesNotContain(contextual, row => row.Id is "Avalonia.Controls.Grid:Row" or "Avalonia.Controls.Canvas:Left" or "Avalonia.Controls.DockPanel:Dock" or "Avalonia.Controls.RelativePanel:AlignLeftWithPanel");
            Assert.Contains(contextual, row => row.Name == "IsSharedSizeScope");
            Assert.Contains(contextual, row => row.Name == "Tip");
            Assert.Contains(contextual, row => row.Name == "CustomContextTag" && row.Presentation!.Context is null);
            foreach (var item in new[] { ("GridChild", "Avalonia.Controls.Grid:Row"), ("CanvasChild", "Avalonia.Controls.Canvas:Left"), ("DockChild", "Avalonia.Controls.DockPanel:Dock"), ("RelativeChild", "Avalonia.Controls.RelativePanel:AlignLeftWithPanel") })
            {
                var rows = await scope.Client.GetPropertiesAsync(new PropertiesRequest(await scope.Find(item.Item1), Contextual: true));
                Assert.True(Assert.Single(rows, row => row.Id == item.Item2).Presentation!.Context!.IsApplicable);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Moving_the_same_control_updates_context_metadata_and_notifies_without_a_value_change()
    {
        var child = new Border { Name = "MovingChild" };
        var grid = new Grid { Children = { child } }; var canvas = new Canvas();
        var window = new Window { Content = new StackPanel { Children = { grid, canvas } } }; window.Show();
        try
        {
            await using var scope = await Scope.Create(window); var id = await scope.Find("MovingChild");
            var before = await scope.Client.GetPropertiesAsync(new PropertiesRequest(id, Contextual: true));
            Assert.Contains(before, row => row.Id == "Avalonia.Controls.Grid:Row");
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scope.Client.PropertiesChanged += notification => { if (notification.ObjectId == id) changed.TrySetResult(); };
            grid.Children.Remove(child); canvas.Children.Add(child);
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var after = await scope.Client.GetPropertiesAsync(new PropertiesRequest(id, Contextual: true));
            Assert.DoesNotContain(after, row => row.Id == "Avalonia.Controls.Grid:Row");
            Assert.Contains(after, row => row.Id == "Avalonia.Controls.Canvas:Left");
            Assert.Equal(id, await scope.Find("MovingChild"));
            canvas.Children.Remove(child);
            var detached = await scope.Client.GetPropertiesAsync(new PropertiesRequest(id, Contextual: true));
            Assert.DoesNotContain(detached, row => row.Id is "Avalonia.Controls.Grid:Row" or "Avalonia.Controls.Canvas:Left");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Template_parts_use_the_actual_visual_layout_parent_instead_of_the_template_owner()
    {
        Border? part = null;
        var button = new Button { Name = "TemplateOwner", Template = new FuncControlTemplate<Button>((_, _) =>
        {
            part = new Border { Name = "GridTemplatePart" };
            return new Grid { Children = { part } };
        }) };
        var window = new Window { Content = button }; window.Show(); window.UpdateLayout();
        try
        {
            Assert.NotNull(part); Assert.IsType<Grid>(part.GetVisualParent());
            Assert.Same(button, part.TemplatedParent);
            await using var scope = await Scope.Create(window);
            var rows = await scope.Client.GetPropertiesAsync(new PropertiesRequest(await scope.Find("GridTemplatePart", TreeKind.Visual), Contextual: true));
            var context = Assert.Single(rows, row => row.Id == "Avalonia.Controls.Grid:Row").Presentation!.Context!;
            Assert.True(context.IsApplicable); Assert.Equal(typeof(Grid).FullName, context.VisualParentType);
        }
        finally { window.Close(); }
    }
    private sealed class CustomOwner : AvaloniaObject
    {
        public static readonly AttachedProperty<int> TagProperty = AvaloniaProperty.RegisterAttached<CustomOwner, Control, int>("CustomContextTag");
    }
    private sealed record Scope(DiagnosticsAgent Agent, DiagnosticsClient Client) : IAsyncDisposable
    {
        public static async Task<Scope> Create(Window window)
        {
            var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions { PipeName = "context-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            var client = new DiagnosticsClient(); await client.ConnectAsync(agent.PipeName); return new(agent, client);
        }
        public async Task<long> Find(string name, TreeKind kind = TreeKind.Logical) => Assert.Single((await Client.SearchTreeAsync(new(name, kind))).Matches, node => node.Name == name).Id;
        public async ValueTask DisposeAsync() { await Client.DisposeAsync(); await Agent.DisposeAsync(); }
    }
}
