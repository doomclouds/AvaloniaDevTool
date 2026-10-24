using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class PropertyEditorTests
{
    [AvaloniaFact]
    public async Task CLR_checkbox_explicitly_enables_getters_and_keeps_default_selection_lazy()
    {
        var probe = new EditorProbe { Name = "Probe" };
        await using var scope = await Scope.Create(probe, false);
        Assert.Equal(0, probe.GetterCalls);
        Assert.DoesNotContain(scope.ViewModel.FilteredProperties, row => row.Name == nameof(EditorProbe.ReadOnlyGetter));
        var inspector = new MainWindow { DataContext = scope.ViewModel }; inspector.Show();
        try
        {
            var checkbox = Assert.Single(inspector.GetVisualDescendants().OfType<CheckBox>(),
                control => AutomationProperties.GetAutomationId(control) == "Inspector.IncludeClrProperties");
            Assert.False(checkbox.IsChecked);
            checkbox.IsChecked = true;
            await Until(() => scope.ViewModel.FilteredProperties.Any(row => row.Name == nameof(EditorProbe.ReadOnlyGetter)));
            Assert.True(probe.GetterCalls > 0);
            Assert.True(scope.ViewModel.FilteredProperties.Single(row => row.Name == nameof(EditorProbe.ReadOnlyGetter)).IsReadOnly);
        }
        finally { inspector.Close(); }
    }

    [AvaloniaFact]
    public async Task Solid_brush_uses_color_picker_without_mutating_brush_until_apply()
    {
        var original = new SolidColorBrush(Colors.Red);
        var probe = new EditorProbe { Name = "Probe", Background = original };
        await using var scope = await Scope.Create(probe);
        var row = scope.ViewModel.FilteredProperties.Single(property => property.Name == "Background");
        Assert.Equal(PropertyEditorKind.SolidColorBrush, row.EditorKind);
        var editor = new PropertyValueEditor { DataContext = row };
        var window = new Window { Content = editor }; window.Show();
        try
        {
            Assert.Single(editor.GetVisualDescendants().OfType<ColorPicker>()).Color = Colors.Blue;
            Assert.Same(original, probe.Background); Assert.Equal(Colors.Red, original.Color);
            row.ApplyCommand.Execute(null);
            await Until(() => row.CanClear);
            Assert.Equal(Colors.Blue, Assert.IsAssignableFrom<ISolidColorBrush>(probe.Background).Color);
            Assert.Equal(Colors.Red, original.Color);
            row.ClearCommand.Execute(null);
            await Until(() => !row.CanClear);
            Assert.Same(original, probe.Background);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Color_picker_changes_only_draft_and_clear_restores_latest_bound_color()
    {
        var model = new ColorModel { Tint = Colors.Red };
        var probe = new EditorProbe { Name = "Probe" };
        probe.Bind(EditorProbe.TintProperty, new Binding(nameof(ColorModel.Tint)) { Source = model });
        await using var scope = await Scope.Create(probe);
        var tint = scope.ViewModel.FilteredProperties.Single(row => row.Name == nameof(EditorProbe.Tint));
        var editor = new PropertyValueEditor { DataContext = tint };
        var editorWindow = new Window { Content = editor, Width = 400, Height = 250 }; editorWindow.Show();
        try
        {
            var picker = Assert.Single(editor.GetVisualDescendants().OfType<ColorPicker>());
            Assert.True(picker.IsVisible);
            picker.Color = Colors.Green;
            Assert.Equal(Colors.Green, tint.DraftColor);
            Assert.True(tint.IsDirty);
            Assert.Equal(Colors.Red, probe.Tint);
            model.Tint = Colors.Blue;
            await Until(() => tint.CurrentValue == Colors.Blue.ToString());
            Assert.Equal(Colors.Green, picker.Color);
            Assert.Equal(Colors.Green, tint.DraftColor);
            tint.ApplyCommand.Execute(null);
            await Until(() => probe.Tint == Colors.Green && tint.CanClear);
            model.Tint = Colors.Yellow;
            Assert.Equal(Colors.Green, probe.Tint);
            tint.ClearCommand.Execute(null);
            try { await Until(() => probe.Tint == Colors.Yellow && !tint.CanClear); }
            catch (OperationCanceledException)
            {
                Assert.Fail($"Clear did not settle. Target={probe.Tint}; model={model.Tint}; current={tint.CurrentValue}; draft={tint.Draft}; clear={tint.CanClear}; same={scope.ViewModel.FilteredProperties.Contains(tint)}; replacement={scope.ViewModel.FilteredProperties.FirstOrDefault(row => row.Id == tint.Id)?.CurrentValue}; error={tint.Error}; selected={scope.ViewModel.SelectedNode?.Id}; connected={scope.ViewModel.IsConnected}; status={scope.ViewModel.Status}");
            }
            tint.Draft = "Not a color";
            Assert.True(tint.HasDraftValidation);
            Assert.Equal("Not a color", tint.Draft);
        }
        finally { editorWindow.Close(); }
    }

    [AvaloniaFact]
    public async Task Boolean_numeric_enum_and_flags_AXAML_controls_share_unapplied_drafts()
    {
        var probe = new EditorProbe { Name = "Probe", Width = 100 };
        await using var scope = await Scope.Create(probe);
        var boolean = scope.ViewModel.FilteredProperties.Single(row => row.Name == "IsEnabled");
        var number = scope.ViewModel.FilteredProperties.Single(row => row.Name == "Width");
        var enumeration = scope.ViewModel.FilteredProperties.Single(row => row.Name == nameof(EditorProbe.Mode));
        var flags = scope.ViewModel.FilteredProperties.Single(row => row.Name == nameof(EditorProbe.Options));
        var host = new StackPanel();
        foreach (var row in new[] { boolean, number, enumeration, flags }) host.Children.Add(new PropertyValueEditor { DataContext = row });
        var editorWindow = new Window { Content = host, Width = 500, Height = 650 }; editorWindow.Show();
        try
        {
            var controls = host.GetVisualDescendants().OfType<Control>().ToArray();
            Find<CheckBox>(controls, "Inspector.BooleanPropertyEditor").IsChecked = false;
            Find<NumericUpDown>(controls, "Inspector.NumberPropertyEditor").Value = 123;
            Find<ComboBox>(controls, "Inspector.EnumPropertyEditor").SelectedItem = nameof(ProbeMode.Second);
            flags.FlagChoices.Single(choice => choice.Name == nameof(ProbeOptions.First)).IsSelected = true;
            flags.FlagChoices.Single(choice => choice.Name == nameof(ProbeOptions.Second)).IsSelected = true;
            Assert.False(boolean.BooleanDraft); Assert.Equal(123, number.NumberDraft);
            Assert.Equal(nameof(ProbeMode.Second), enumeration.Draft);
            Assert.All(new[] { boolean, number, enumeration, flags }, row => Assert.True(row.IsDirty));
            Assert.True(probe.IsEnabled); Assert.Equal(100, probe.Width);
            Assert.Equal(ProbeMode.First, probe.Mode); Assert.Equal(ProbeOptions.None, probe.Options);
            foreach (var row in new[] { boolean, number, enumeration, flags })
            {
                row.ApplyCommand.Execute(null);
                await Until(() => row.CanClear);
            }
            Assert.False(probe.IsEnabled); Assert.Equal(123, probe.Width);
            Assert.Equal(ProbeMode.Second, probe.Mode); Assert.Equal(ProbeOptions.First | ProbeOptions.Second, probe.Options);
        }
        finally { editorWindow.Close(); }
    }

    [AvaloniaFact]
    public async Task Sorting_filtering_and_external_updates_keep_selected_row_and_dirty_draft()
    {
        var probe = new EditorProbe { Name = "Probe", Width = 100 };
        await using var scope = await Scope.Create(probe);
        var viewModel = scope.ViewModel;
        var inspector = new MainWindow { DataContext = viewModel };
        inspector.Show();
        var width = viewModel.FilteredProperties.Single(row => row.Name == "Width");
        width.Draft = "222";
        viewModel.SelectedProperty = width;
        foreach (var order in new[] { 1, 2, 3, 4, 0 })
        {
            viewModel.PropertySortIndex = order;
            Assert.Same(width, viewModel.SelectedProperty);
            Assert.Same(width, inspector.FindControl<ListBox>("PropertyList")!.SelectedItem);
            Assert.Same(width, viewModel.FilteredProperties.Single(row => row.Id == width.Id));
        }
        viewModel.PropertyCategory = width.Category;
        viewModel.PropertyFilter = "Width";
        Assert.Contains(width, viewModel.FilteredProperties);
        probe.Width = 321;
        await Until(() => width.CurrentValue == "321");
        Assert.Equal("222", width.Draft); Assert.True(width.IsDirty);
        Assert.Same(width, viewModel.SelectedProperty);
        viewModel.PropertySortIndex = 4;
        var group = Assert.IsType<PropertyCategoryGroupViewModel>(width.Group);
        group.IsExpanded = false;
        Assert.False(width.IsGroupExpanded);
        Assert.True(viewModel.FilteredProperties.First(row => row.Category == width.Category).ShowCategoryHeader);
        Assert.Single(viewModel.FilteredProperties.Where(row => row.Category == width.Category), row => row.IsVisibleInGroup);
        viewModel.RefreshCommand.Execute(null);
        await Until(() => viewModel.RefreshCommand.CanExecute(null));
        Assert.Same(width, viewModel.FilteredProperties.Single(row => row.Id == width.Id));
        Assert.Same(group, width.Group);
        Assert.False(group.IsExpanded);
        probe.Width = 322;
        try { await Until(() => width.CurrentValue == "322"); }
        catch (OperationCanceledException) { Assert.Fail($"Grouped update did not settle. Current={width.CurrentValue}; target={probe.Width}; same={viewModel.FilteredProperties.Contains(width)}; replacement={viewModel.FilteredProperties.FirstOrDefault(row => row.Id == width.Id)?.CurrentValue}; selected={viewModel.SelectedNode?.Id}; connected={viewModel.IsConnected}; status={viewModel.Status}"); }
        Assert.Same(group, width.Group); Assert.False(group.IsExpanded);
        Assert.Equal("222", width.Draft);
        group.IsExpanded = true;
        viewModel.PropertyFilter = ""; viewModel.PropertyCategory = "全部分类";
        var note = viewModel.FilteredProperties.Single(row => row.Name == nameof(EditorProbe.ReadOnlyNote));
        Assert.True(note.IsReadOnly);
        var editor = new PropertyValueEditor { DataContext = note };
        var editorWindow = new Window { Content = editor }; editorWindow.Show();
        try { Assert.DoesNotContain(editor.GetVisualDescendants().OfType<Control>(), control => control.IsEffectivelyVisible && control is TextBox or ColorPicker or NumericUpDown or CheckBox or ComboBox); }
        finally { editorWindow.Close(); inspector.Close(); }
    }

    [AvaloniaFact]
    public async Task Property_preview_command_loads_image_from_property_value()
    {
        using var image = new RenderTargetBitmap(new PixelSize(24, 12));
        using (var context = image.CreateDrawingContext()) context.FillRectangle(Brushes.Red, new Rect(0, 0, 24, 12));
        var probe = new EditorProbe { Name = "Probe", Photo = image };
        await using var scope = await Scope.Create(probe);
        var row = scope.ViewModel.FilteredProperties.Single(property => property.Name == nameof(EditorProbe.Photo));
        Assert.True(row.HasPreview); Assert.True(row.IsReadOnly);
        row.PreviewCommand.Execute(null);
        await Until(() => row.HasPreviewImage);
        Assert.Equal(new PixelSize(24, 12), row.PreviewImage!.PixelSize);
        Assert.Same(image, probe.Photo);
        Assert.False(row.IsDirty);
    }

    private static T Find<T>(IEnumerable<Control> controls, string id) where T : Control =>
        Assert.Single(controls.OfType<T>(), control => control.IsEffectivelyVisible && AutomationProperties.GetAutomationId(control) == id);
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(15, timeout.Token);
    }
    private enum ProbeMode { First, Second }
    [Flags] private enum ProbeOptions { None, First = 1, Second = 2 }
    private sealed class EditorProbe : Border
    {
        public static readonly StyledProperty<Color> TintProperty = AvaloniaProperty.Register<EditorProbe, Color>(nameof(Tint), Colors.Red);
        public static readonly StyledProperty<ProbeMode> ModeProperty = AvaloniaProperty.Register<EditorProbe, ProbeMode>(nameof(Mode));
        public static readonly StyledProperty<ProbeOptions> OptionsProperty = AvaloniaProperty.Register<EditorProbe, ProbeOptions>(nameof(Options));
        [Category("Probe")]
        public Color Tint { get => GetValue(TintProperty); set => SetValue(TintProperty, value); }
        public ProbeMode Mode { get => GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
        public ProbeOptions Options { get => GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
        public string ReadOnlyNote => "Read only";
        public IImage? Photo { get; set; }
        public int GetterCalls;
        public string ReadOnlyGetter { get { GetterCalls++; return "Explicit CLR value"; } }
    }
    private sealed class ColorModel : INotifyPropertyChanged
    {
        private Color _tint;
        public event PropertyChangedEventHandler? PropertyChanged;
        public Color Tint { get => _tint; set { _tint = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tint))); } }
    }
    private sealed class Scope(Window window, DiagnosticsAgent agent, MainWindowViewModel viewModel) : IAsyncDisposable
    {
        public MainWindowViewModel ViewModel => viewModel;
        public static async Task<Scope> Create(Control content, bool includeClr = true)
        {
            var window = new Window { Content = content, Width = 160, Height = 120 }; window.Show();
            var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "avdt-property-editor-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            var viewModel = new MainWindowViewModel(agent.PipeName) { SelectedTreeIndex = 1, IncludeClrProperties = includeClr };
            await viewModel.InitializeAsync();
            viewModel.SelectedNode = Assert.Single(viewModel.Roots).Children.Single(node => node.Node.Name == content.Name);
            await Until(() => viewModel.FilteredProperties.Any(row => row.Name == "Width"));
            return new Scope(window, agent, viewModel);
        }
        public async ValueTask DisposeAsync() { await viewModel.DisposeAsync(); await agent.DisposeAsync(); window.Close(); }
    }
}
