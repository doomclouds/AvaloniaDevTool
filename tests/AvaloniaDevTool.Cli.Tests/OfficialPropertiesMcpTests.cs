using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class OfficialPropertiesMcpTests
{
    [AvaloniaFact]
    public async Task Stdio_props_filter_real_values_and_set_null_unset_preserve_live_bindings()
    {
        var model = new Model { Text = "bound", Enabled = true };
        var text = new TextBox { Name = "OfficialText" };
        TextSearch.SetText(text, "attached search seed");
        text.Bind(TextBox.TextProperty, new Binding(nameof(Model.Text)) { Source = model });
        var check = new CheckBox { Name = "OfficialNullable", IsThreeState = true, IsChecked = true };
        var styled = new Border { Name = "OfficialStyled" };
        await using var target = await Target.CreateAsync(new StackPanel { Children = { text, check, styled } }, window =>
            window.Styles.Add(new Style(selector => selector.OfType<Border>().Name(styled.Name!))
            { Setters = { new Setter(Border.BackgroundProperty, Brushes.Crimson), new Setter(Layoutable.WidthProperty, 85d) } }));
        var id = await target.IdAsync(text.Name!);
        var all = await target.PropsAsync(id);
        Assert.Equal(2, all.Count(property => property.Name == "Text"));
        var nullFilter = await target.PropsAsync(id, null, true);
        Assert.Equal(all.Select(property => property.Id), nullFilter.Select(property => property.Id));
        var filtered = await target.PropsAsync(id, ["Text", null]);
        Assert.Equal("bound", Assert.Single(filtered).Value);
        Assert.True(Assert.Single(filtered).HasClrWrapper);
        Assert.Equal("attached search seed", Assert.Single(await target.PropsAsync(id, ["Avalonia.Controls.Primitives.TextSearch:Text"])).Value);
        Assert.Empty(await target.PropsAsync(id, [null, "not-a-property"]));
        Assert.Null(Assert.Single(await target.PropsAsync(id, ["Tag"])).Value);
        var styledId = await target.IdAsync(styled.Name!);
        var width = Assert.Single(await target.PropsAsync(styledId, ["Width"]));
        Assert.Equal("85", width.Value); Assert.Contains("Style", width.ValueSource);

        await target.SetAsync(id, "Text", "plain <text> {literal}");
        Assert.Equal("plain <text> {literal}", text.Text);
        Assert.Equal("attached search seed", TextSearch.GetText(text));
        Assert.Equal(text.Text, Assert.Single(await target.PropsAsync(id, ["Text"])).Value);
        await target.SetAsync(id, "Text", "null");
        Assert.Null(text.Text); Assert.Null(Assert.Single(await target.PropsAsync(id, ["Text"])).Value);
        Assert.True(Assert.Single(await target.PropsAsync(id, ["Text"])).CanClear);
        model.Text = "changed underneath";
        Assert.Null(text.Text);
        await target.SetAsync(id, "Text", "unset");
        Assert.Equal("changed underneath", text.Text);
        Assert.False(Assert.Single(await target.PropsAsync(id, ["Text"])).CanClear);
        model.Text = "still live"; Assert.Equal("still live", text.Text);

        var checkId = await target.IdAsync(check.Name!);
        await target.SetAsync(checkId, "IsChecked", "null"); Assert.Null(check.IsChecked);
        await target.SetAsync(checkId, "IsChecked", "false"); Assert.False(check.IsChecked);
        await target.SetAsync(checkId, "IsChecked", "true"); Assert.True(check.IsChecked);
        var invalid = await target.CallAsync("set-prop", new() { ["nodeId"] = checkId, ["propertyName"] = "IsChecked", ["value"] = "bad-bool" });
        Assert.True(invalid.IsError); Assert.Contains("conversion_failed", Text(invalid)); Assert.True(check.IsChecked);
        var jsonNull = await target.CallAsync("set-prop", new() { ["nodeId"] = id, ["propertyName"] = "Text", ["value"] = null });
        Assert.True(jsonNull.IsError); Assert.Equal("still live", text.Text);
        var missing = await target.CallAsync("set-prop", new() { ["nodeId"] = id, ["propertyName"] = "Missing", ["value"] = "x" });
        Assert.True(missing.IsError); Assert.Contains("property_not_found", Text(missing));
        await target.SetAsync(styledId, "Background", "null"); Assert.Null(styled.Background);
        await target.SetAsync(styledId, "Background", "unset"); Assert.Same(Brushes.Crimson, styled.Background);
    }

    [AvaloniaFact]
    public async Task Stdio_set_prop_loads_full_xmlns_object_and_rejects_invalid_xaml_without_partial_write()
    {
        var border = new Border { Name = "OfficialXamlBrush", Background = Brushes.Red };
        var text = new TextBlock { Name = "OfficialLiteral" };
        await using var target = await Target.CreateAsync(new StackPanel { Children = { border, text } });
        var id = await target.IdAsync(border.Name!);
        const string xaml = "<LinearGradientBrush xmlns=\"https://github.com/avaloniaui\" StartPoint=\"0%,0%\" EndPoint=\"100%,0%\"><GradientStop Color=\"#FFFF0000\" Offset=\"0\"/><GradientStop Color=\"#FF0000FF\" Offset=\"1\"/></LinearGradientBrush>";
        await target.SetAsync(id, "Background", xaml, true);
        var brush = Assert.IsType<LinearGradientBrush>(border.Background);
        Assert.Equal(2, brush.GradientStops.Count); Assert.Equal(Colors.Red, brush.GradientStops[0].Color);
        Assert.Equal(Colors.Blue, brush.GradientStops[1].Color);
        Assert.Contains("LinearGradientBrush", Assert.Single(await target.PropsAsync(id, ["Background"])).Value);
        foreach (var invalid in new[]
        {
            "<SolidColorBrush xmlns=\"https://github.com/avaloniaui\" Color=\"invalid-color\"/>",
            "<SolidColorBrush xmlns=\"https://github.com/avaloniaui\" Color=\"Blue\">",
            "<TextBlock xmlns=\"https://github.com/avaloniaui\" Text=\"wrong type\"/>",
            "<SolidColorBrush Color=\"Blue\"/>"
        })
        {
            var result = await target.CallAsync("set-prop", new() { ["nodeId"] = id, ["propertyName"] = "Background", ["value"] = invalid, ["isXamlValue"] = true });
            Assert.True(result.IsError); Assert.Same(brush, border.Background);
            Assert.True(Assert.Single(await target.PropsAsync(id, ["Background"])).CanClear);
        }
        var textId = await target.IdAsync(text.Name!);
        await target.SetAsync(textId, "Text", xaml, false); Assert.Equal(xaml, text.Text);
        await target.SetAsync(textId, "Text", "<x:String xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">null</x:String>", true);
        Assert.Equal("null", text.Text);
        await target.SetAsync(id, "Background", "unset"); Assert.Same(Brushes.Red, border.Background);
    }

    [AvaloniaFact]
    public async Task Stdio_styles_filter_real_setters_and_include_metadata_defaults_and_template_binding()
    {
        var border = new Border { Name = "OfficialStyleTarget" };
        var templateBorder = new Border { Name = "OfficialTemplatePart" };
        templateBorder.Bind(Border.BackgroundProperty, new TemplateBinding(TemplatedControl.BackgroundProperty));
        var button = new Button { Name = "OfficialTemplateOwner", Background = Brushes.Gold,
            Template = new FuncControlTemplate<Button>((_, _) => templateBorder) };
        await using var target = await Target.CreateAsync(new StackPanel { Children = { border, button } }, window =>
            window.Styles.Add(new Style(selector => selector.OfType<Border>().Name(border.Name!))
            { Setters = { new Setter(Border.BackgroundProperty, Brushes.Crimson), new Setter(Layoutable.WidthProperty, 85d) } }));
        button.ApplyTemplate(); target.Window.UpdateLayout();
        var id = await target.IdAsync(border.Name!);
        var all = Read<StyleSnapshot>(await target.CallAsync("styles", new() { ["nodeId"] = id }));
        var explicitNull = Read<StyleSnapshot>(await target.CallAsync("styles", new() { ["nodeId"] = id, ["propertyNames"] = null }));
        Assert.Equal(all.Frames.SelectMany(frame => frame.Setters), explicitNull.Frames.SelectMany(frame => frame.Setters));
        Assert.Contains(all.Frames.SelectMany(frame => frame.Setters), setter => setter.PropertyName == "Background" && setter.MatchesEffectiveValue == true);
        Assert.DoesNotContain(all.Frames, frame => frame.Kind == "Default");
        var filtered = Read<StyleSnapshot>(await target.CallAsync("styles", new() { ["nodeId"] = id, ["propertyNames"] = new string?[] { "Width", null } }));
        Assert.NotEmpty(filtered.Frames); Assert.All(filtered.Frames.SelectMany(frame => frame.Setters), setter => Assert.Equal("Width", setter.PropertyName));
        var defaults = Read<StyleSnapshot>(await target.CallAsync("styles", new() { ["nodeId"] = id, ["propertyNames"] = new[] { "Width" }, ["includeDefaults"] = true }));
        var defaultWidth = Assert.Single(Assert.Single(defaults.Frames, frame => frame.Kind == "Default").Setters);
        Assert.Equal("Width", defaultWidth.PropertyName); Assert.Equal("NaN", defaultWidth.Value); Assert.False(defaultWidth.MatchesEffectiveValue);
        var empty = Read<StyleSnapshot>(await target.CallAsync("styles", new() { ["nodeId"] = id, ["propertyNames"] = new string?[] { null, "NotAProperty" }, ["includeDefaults"] = true }));
        Assert.Empty(empty.Frames); Assert.Empty(empty.Bindings);
        var partId = await target.IdAsync(templateBorder.Name!, true);
        var part = Read<StyleSnapshot>(await target.CallAsync("styles", new() { ["nodeId"] = partId, ["propertyNames"] = new[] { "Background" } }));
        var binding = Assert.Single(part.Bindings);
        Assert.Equal("template-binding", binding.Details!.Kind); Assert.Same(Brushes.Gold, templateBorder.Background);
        Assert.True(binding.Details.Source.ObjectId > uint.MaxValue);
    }

    [AvaloniaFact]
    public async Task Stdio_pseudo_class_and_actions_change_styles_focus_enablement_and_scroll_visibility()
    {
        var pseudo = new PseudoProbe { Name = "OfficialPseudo", Height = 20 };
        var input = new TextBox { Name = "OfficialFocus", Height = 30 };
        var model = new Model { Enabled = true };
        input.Bind(InputElement.IsEnabledProperty, new Binding(nameof(Model.Enabled)) { Source = model });
        var offscreen = new Button { Name = "OfficialOffscreen", Content = "bottom", Height = 30 };
        var scroll = new ScrollViewer { Name = "OfficialScroll", Height = 100, Width = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new StackPanel { Children = { new Border { Height = 1400 }, offscreen } } };
        await using var target = await Target.CreateAsync(new StackPanel { Children = { pseudo, input, scroll } }, window =>
        {
            window.Styles.Add(new Style(selector => selector.OfType<PseudoProbe>()) { Setters = { new Setter(Border.BackgroundProperty, Brushes.Blue) } });
            window.Styles.Add(new Style(selector => selector.OfType<PseudoProbe>().Class(":probe")) { Setters = { new Setter(Border.BackgroundProperty, Brushes.Red) } });
        });
        var pseudoId = await target.IdAsync(pseudo.Name!);
        var listed = Read<PseudoClassInfo[]>(await target.CallAsync("pseudo-class", new() { ["nodeId"] = pseudoId }));
        Assert.Contains(listed, item => item.Name == ":probe" && item.IsDeclared && !item.IsActive);
        Assert.DoesNotContain(listed, item => item.Name == ":pressed");
        await target.CallSuccessAsync("pseudo-class", new() { ["nodeId"] = pseudoId, ["pseudoClass"] = "probe", ["isActive"] = true });
        Assert.Same(Brushes.Red, pseudo.Background);
        await target.CallSuccessAsync("pseudo-class", new() { ["nodeId"] = pseudoId, ["pseudoClass"] = ":probe", ["isActive"] = false });
        Assert.Same(Brushes.Blue, pseudo.Background);
        var readOnly = Read<PseudoClassInfo[]>(await target.CallAsync("pseudo-class", new() { ["nodeId"] = pseudoId, ["pseudoClass"] = "probe", ["isActive"] = null }));
        Assert.False(Assert.Single(readOnly, item => item.Name == ":probe").IsActive);
        await target.CallSuccessAsync("pseudo-class", new() { ["nodeId"] = pseudoId, ["pseudoClass"] = null, ["isActive"] = true });
        Assert.Same(Brushes.Blue, pseudo.Background);

        var inputId = await target.IdAsync(input.Name!);
        Assert.True(Read<ElementActionResult>(await target.CallAsync("action", new() { ["nodeId"] = inputId, ["action"] = "Focus" })).Succeeded);
        Assert.Same(input, target.Window.FocusManager!.GetFocusedElement());
        await target.CallSuccessAsync("input", new() { ["nodeId"] = inputId, ["inputEvent"] = "Text", ["arg"] = "focused input" });
        Assert.Equal("focused input", input.Text);
        await target.CallSuccessAsync("action", new() { ["nodeId"] = inputId, ["action"] = "Disable" }); Assert.False(input.IsEnabled);
        Assert.False(Read<ElementActionResult>(await target.CallAsync("action", new() { ["nodeId"] = inputId, ["action"] = "Focus" })).Succeeded);
        await target.CallSuccessAsync("action", new() { ["nodeId"] = inputId, ["action"] = "Enable" }); Assert.True(input.IsEnabled);
        model.Enabled = false; Assert.True(input.IsEnabled);
        await target.SetAsync(inputId, "IsEnabled", "unset"); Assert.False(input.IsEnabled);
        var offscreenId = await target.IdAsync(offscreen.Name!);
        target.Window.UpdateLayout(); Assert.Equal(0, scroll.Offset.Y);
        Assert.True(offscreen.TranslatePoint(default, scroll)!.Value.Y > scroll.Bounds.Height);
        await target.CallSuccessAsync("action", new() { ["nodeId"] = offscreenId, ["action"] = "BringIntoView" });
        target.Window.UpdateLayout(); Assert.True(scroll.Offset.Y > 0);
        Assert.InRange(offscreen.TranslatePoint(default, scroll)!.Value.Y, -offscreen.Bounds.Height, scroll.Bounds.Height);
        var badAction = await target.CallAsync("action", new() { ["nodeId"] = inputId, ["action"] = "Bogus" }); Assert.True(badAction.IsError);
    }

    private static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text));
    private static T Read<T>(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        var node = result.StructuredContent is { } structured ? JsonNode.Parse(JsonSerializer.Serialize(structured)) : JsonNode.Parse(Text(result));
        if (node is JsonObject obj && obj.TryGetPropertyValue("result", out var wrapped)) node = wrapped;
        var options = new JsonSerializerOptions(ProtocolJson.Options) { Converters = { new JsonStringEnumConverter() } };
        return node!.Deserialize<T>(options)!;
    }
    [PseudoClasses(":probe")]
    private sealed class PseudoProbe : Border;
    private sealed class Model : INotifyPropertyChanged
    {
        private string? _text; private bool _enabled;
        public string? Text { get => _text; set { _text = value; PropertyChanged?.Invoke(this, new(nameof(Text))); } }
        public bool Enabled { get => _enabled; set { _enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private sealed class Target(Window window, DiagnosticsAgent agent, McpClient client) : IAsyncDisposable
    {
        public Window Window { get; } = window;
        public static async Task<Target> CreateAsync(Control content, Action<Window>? configure = null)
        {
            var window = new Window { Width = 420, Height = 320, Content = content };
            configure?.Invoke(window); window.Show(); window.UpdateLayout();
            var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            { PipeName = "official-properties-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window] });
            var assembly = typeof(OfficialPropertiesMcpTests).Assembly.Location;
            try
            {
                var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = "dotnet", Name = "Official properties integration",
                    Arguments = ["exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"), "--depsfile", Path.ChangeExtension(assembly, ".deps.json"),
                        typeof(Program).Assembly.Location, "mcp", "--profile", "official", "--pipe", agent.PipeName], ShutdownTimeout = TimeSpan.FromSeconds(5)
                }));
                return new Target(window, agent, client);
            }
            catch { await agent.DisposeAsync(); window.Close(); throw; }
        }
        public Task<CallToolResult> CallAsync(string name, Dictionary<string, object?> args) => client.CallToolAsync(name, args).AsTask();
        public async Task CallSuccessAsync(string name, Dictionary<string, object?> args)
        {
            var result = await CallAsync(name, args);
            Assert.False(result.IsError == true, Text(result));
        }
        public Task SetAsync(long id, string property, string value, bool xaml = false) => CallSuccessAsync("set-prop",
            new() { ["nodeId"] = id, ["propertyName"] = property, ["value"] = value, ["isXamlValue"] = xaml });
        public async Task<PropertyInfoDto[]> PropsAsync(long id, string?[]? names = null, bool explicitNull = false)
        {
            var args = new Dictionary<string, object?> { ["nodeId"] = id };
            if (names is not null || explicitNull) args["propertyNames"] = names;
            return Read<PropertyInfoDto[]>(await CallAsync("props", args));
        }
        public async Task<long> IdAsync(string name, bool templates = false)
        {
            var found = Read<TreeSearchResult>(await CallAsync("search", new() { ["query"] = name, ["searchTemplates"] = templates }));
            return Assert.Single(found.Matches, item => item.Name == name).Id;
        }
        public async ValueTask DisposeAsync()
        { await client.DisposeAsync(); await agent.DisposeAsync(); Window.Close(); }
    }
}
