using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Agent;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class InspectorIntegrationTests
{
    [AvaloniaFact]
    public void Window_markup_exposes_connection_tree_and_property_controls()
    {
        var window = new MainWindow();
        Assert.NotNull(window.FindControl<TextBox>("PipeNameBox"));
        Assert.NotNull(window.FindControl<TreeView>("ControlTree"));
        Assert.NotNull(window.FindControl<ListBox>("PropertyList"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Inspector_connects_loads_children_and_edits_then_restores_binding()
    {
        var source = new TextModel { Text = "Original" };
        var text = new TextBlock { Name = "Message" };
        text.Bind(TextBlock.TextProperty, new Binding(nameof(TextModel.Text)) { Source = source });
        var panel = new StackPanel { Name = "SamplePanel", Children = { text } };
        var target = new Window { Content = panel };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var viewModel = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await viewModel.InitializeAsync();
            Assert.True(viewModel.IsConnected, viewModel.Status);
            var root = Assert.Single(viewModel.Roots);
            var panelNode = Assert.Single(root.Children, node => node.Node.Name == "SamplePanel");
            panelNode.IsExpanded = true;
            await WaitUntilAsync(() => panelNode.Children.Any(node => node.Node.Name == "Message"));
            viewModel.SelectedNode = panelNode.Children.Single(node => node.Node.Name == "Message");
            await WaitUntilAsync(() => viewModel.FilteredProperties.Any(IsTextProperty));
            var property = viewModel.FilteredProperties.Single(IsTextProperty);
            property.Draft = "Temporary";
            property.ApplyCommand.Execute(null);
            await WaitUntilAsync(() => text.Text == "Temporary" && property.CanClear);
            source.Text = "Updated binding";
            Assert.Equal("Temporary", text.Text);
            property.ClearCommand.Execute(null);
            await WaitUntilAsync(() => text.Text == "Updated binding" && !property.CanClear);
            viewModel.DisconnectCommand.Execute(null);
            await WaitUntilAsync(() => !viewModel.IsConnected);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Live_updates_preserve_dirty_draft_and_existing_property_row()
    {
        var text = new TextBlock { Name = "Message", Text = "Original" };
        var target = new Window { Content = text };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var viewModel = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await viewModel.InitializeAsync();
            Assert.True(viewModel.IsConnected, viewModel.Status);
            var root = Assert.Single(viewModel.Roots);
            viewModel.SelectedNode = root.Children.Single(node => node.Node.Name == "Message");
            await WaitUntilAsync(() => viewModel.FilteredProperties.Any(IsTextProperty));
            var property = viewModel.FilteredProperties.Single(IsTextProperty);
            property.Draft = "Unapplied draft";
            var removals = 0;
            viewModel.FilteredProperties.CollectionChanged += (_, change) =>
            {
                if (change.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Replace)
                    removals++;
            };
            text.Text = "External update";
            await WaitUntilAsync(() => property.CurrentValue == "External update");
            Assert.Same(property, viewModel.FilteredProperties.Single(IsTextProperty));
            Assert.Equal("Unapplied draft", property.Draft);
            Assert.True(property.IsDirty);
            Assert.Equal(0, removals);
        }
        finally { target.Close(); }
    }

    [AvaloniaFact]
    public async Task Invalid_property_edit_displays_row_error_without_changing_target()
    {
        var text = new TextBlock { Name = "Message", Width = 120 };
        var target = new Window { Content = text };
        target.Show();
        try
        {
            await using var agent = CreateAgent(target);
            await using var viewModel = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1 };
            await viewModel.InitializeAsync();
            Assert.True(viewModel.IsConnected, viewModel.Status);
            viewModel.SelectedNode = Assert.Single(viewModel.Roots).Children.Single(node => node.Node.Name == "Message");
            await WaitUntilAsync(() => viewModel.FilteredProperties.Any(row => row.Name == "Width"));
            var width = viewModel.FilteredProperties.Single(row => row.Name == "Width");
            width.Draft = "invalid width";
            width.ApplyCommand.Execute(null);
            await WaitUntilAsync(() => width.HasError);
            Assert.Equal(120, text.Width);
            Assert.Contains("conversion_failed", width.Error);
        }
        finally { target.Close(); }
    }

    private static DiagnosticsAgent CreateAgent(Window window) => DiagnosticsAgent.Attach(Application.Current!,
        new DiagnosticsAgentOptions
        {
            PipeName = "inspector-tests-" + Guid.NewGuid().ToString("N"),
            ApplicationName = "Inspector integration test",
            TopLevelProvider = () => [window]
        });

    private static bool IsTextProperty(PropertyRowViewModel property) =>
        property.Name == "Text" && property.DeclaringType == typeof(TextBlock).FullName;

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate())
            await Task.Delay(15, timeout.Token);
    }

    private sealed class TextModel : INotifyPropertyChanged
    {
        private string _text = "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Text
        {
            get => _text;
            set
            {
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }
    }
}

