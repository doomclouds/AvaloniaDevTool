using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class BindingCultureTests
{
    [AvaloniaTheory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void Ordinary_and_compiled_bindings_use_retained_culture_for_format_and_convert_back(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        foreach (var compiled in new[] { false, true })
        {
            var source = new AmountModel();
            var converter = new AmountConverter();
            BindingBase binding = compiled
                ? CompiledBinding.Create<AmountModel, decimal>(model => model.Amount, source, converter,
                    BindingMode.TwoWay, converterCulture: culture, stringFormat: "N2", updateSourceTrigger: UpdateSourceTrigger.Explicit)
                : new Binding(nameof(AmountModel.Amount))
                {
                    Source = source, Converter = converter, ConverterCulture = culture, StringFormat = "N2",
                    Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.Explicit
                };
            var target = new TextBox();
            target.Bind(TextBox.TextProperty, binding);
            WithWindow(target, () =>
            {
                Assert.Equal(1234.5m.ToString("N2", culture), target.Text);
                Assert.Equal(cultureName, converter.ForwardCulture);
                var reads = source.GetterCalls;
                var conversions = converter.ForwardCalls;
                var details = Details(target);
                Assert.Equal("binding", details.Kind);
                Assert.Equal(BindingDetailState.Known, details.ConverterCulture!.State);
                Assert.Equal(cultureName, details.ConverterCulture.Value);
                Assert.Equal("Configured", details.ConverterCultureOrigin!.Value);
                Assert.Contains("_uncommon._converterCulture", details.ConverterCultureOrigin.Reason);
                Assert.Equal("{0:N2}", details.StringFormat.Value);
                Assert.Equal(reads, source.GetterCalls);
                Assert.Equal(conversions, converter.ForwardCalls);
                var input = 98.25m.ToString("0.00", culture);
                target.Text = input;
                Assert.Equal(1234.5m, source.Amount);
                BindingOperations.GetBindingExpressionBase(target, TextBox.TextProperty)!.UpdateSource();
                Assert.Equal(98.25m, source.Amount);
                Assert.Equal(cultureName, converter.BackwardCulture);
                Assert.Equal("Explicit", Details(target).UpdateSourceTrigger.Value);
            });
        }
    }

    [AvaloniaFact]
    public void Unconfigured_culture_stays_null_when_diagnostic_thread_culture_changes()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var withOptions in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var source = new AmountModel();
                var converter = new AmountConverter();
                var target = new TextBlock();
                target.Bind(TextBlock.TextProperty, new Binding(nameof(AmountModel.Amount))
                {
                    Source = source, Converter = withOptions ? converter : null,
                    StringFormat = withOptions ? "N2" : null
                });
                WithWindow(target, () =>
                {
                    var displayed = target.Text;
                    var reads = source.GetterCalls;
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                    var details = Details(target);
                    Assert.Equal(BindingDetailState.Null, details.ConverterCulture!.State);
                    Assert.Null(details.ConverterCulture.Value);
                    Assert.Equal("CurrentCulture fallback", details.ConverterCultureOrigin!.Value);
                    Assert.Contains("not sampled", details.ConverterCultureOrigin.Reason);
                    Assert.Equal(displayed, target.Text);
                    Assert.Equal(reads, source.GetterCalls);
                    if (withOptions)
                    {
                        Assert.Equal("fr-FR", converter.ForwardCulture);
                        Assert.Equal(1234.5m.ToString("N2", CultureInfo.GetCultureInfo("fr-FR")), target.Text);
                    }
                });
            }
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [AvaloniaTheory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void Multi_binding_reports_actual_format_wrapper_and_independent_child_cultures(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var source = new AmountModel();
        var converter = new SumConverter();
        var target = new TextBlock();
        target.Bind(TextBlock.TextProperty, new MultiBinding
        {
            Converter = converter, ConverterCulture = culture, StringFormat = "Total {0:N2}",
            Bindings =
            {
                new Binding(nameof(AmountModel.Amount)) { Source = source, ConverterCulture = CultureInfo.GetCultureInfo("en-US") },
                new Binding(nameof(AmountModel.Quantity)) { Source = source }
            }
        });
        WithWindow(target, () =>
        {
            Assert.Equal("Total " + 1236.5m.ToString("N2", culture), target.Text);
            Assert.Equal(cultureName, converter.Culture);
            var reads = source.GetterCalls;
            var details = Details(target);
            Assert.Equal("multi-binding", details.Kind);
            Assert.Equal(cultureName, details.ConverterCulture!.Value);
            Assert.Equal("Configured", details.ConverterCultureOrigin!.Value);
            Assert.Equal("Total {0:N2}", details.StringFormat.Value);
            Assert.Contains(nameof(StringFormatMultiValueConverter), details.Converter.Value);
            Assert.Equal(BindingDetailState.NotApplicable, details.UpdateSourceTrigger.State);
            Assert.Equal("en-US", details.Children[0].ConverterCulture!.Value);
            Assert.Equal(BindingDetailState.Null, details.Children[1].ConverterCulture!.State);
            Assert.Equal("CurrentCulture fallback", details.Children[1].ConverterCultureOrigin!.Value);
            Assert.Equal(reads, source.GetterCalls);
        });
    }

    [AvaloniaTheory]
    [InlineData("fr-FR")]
    [InlineData("en-US")]
    public void Template_binding_preserves_configured_culture_without_claiming_a_format_option(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var converter = new AmountConverter(formatInConverter: true);
        var button = new Button
        {
            Content = 1234.5m,
            Template = new FuncControlTemplate<Button>((_, _) =>
            {
                var text = new TextBox();
                text.Bind(TextBox.TextProperty, new TemplateBinding(ContentControl.ContentProperty)
                {
                    Mode = BindingMode.TwoWay, Converter = converter, ConverterCulture = culture
                });
                return text;
            })
        };
        WithWindow(button, () =>
        {
            button.ApplyTemplate();
            var target = Assert.Single(button.GetVisualDescendants().OfType<TextBox>());
            Assert.Equal(1234.5m.ToString("N2", culture), target.Text);
            var details = Details(target);
            Assert.Equal("template-binding", details.Kind);
            Assert.Equal(cultureName, details.ConverterCulture!.Value);
            Assert.Equal("Configured", details.ConverterCultureOrigin!.Value);
            Assert.Equal(BindingDetailState.NotApplicable, details.StringFormat.State);
            Assert.Equal(BindingDetailState.NotApplicable, details.UpdateSourceTrigger.State);
            target.Text = 98.25m.ToString("0.00", culture);
            Assert.Equal(98.25m, button.Content);
            Assert.Equal(cultureName, converter.BackwardCulture);
        });
    }

    [AvaloniaTheory]
    [InlineData(UpdateSourceTrigger.Default, "PropertyChanged")]
    [InlineData(UpdateSourceTrigger.PropertyChanged, "PropertyChanged")]
    [InlineData(UpdateSourceTrigger.LostFocus, "LostFocus")]
    [InlineData(UpdateSourceTrigger.Explicit, "Explicit")]
    public void Two_way_text_box_source_updates_follow_real_trigger_for_ordinary_and_compiled_bindings(
        UpdateSourceTrigger trigger, string effectiveTrigger)
    {
        foreach (var compiled in new[] { false, true })
        {
            var source = new NameModel();
            var target = new TextBox();
            var next = new Button { Content = "Next" };
            BindingBase binding = compiled
                ? CompiledBinding.Create<NameModel, string>(model => model.Name, source, mode: BindingMode.TwoWay, updateSourceTrigger: trigger)
                : new Binding(nameof(NameModel.Name)) { Source = source, Mode = BindingMode.TwoWay, UpdateSourceTrigger = trigger };
            target.Bind(TextBox.TextProperty, binding);
            WithWindow(new StackPanel { Children = { target, next } }, () =>
            {
                Assert.Equal("Initial", target.Text);
                Assert.True(target.Focus());
                Assert.True(target.IsFocused);
                target.Text = "Edited";
                Assert.Equal(effectiveTrigger == "PropertyChanged" ? "Edited" : "Initial", source.Name);
                var getterCalls = source.GetterCalls;
                Assert.Equal(effectiveTrigger, Details(target).UpdateSourceTrigger.Value);
                Assert.Equal(getterCalls, source.GetterCalls);
                Assert.True(next.Focus());
                Assert.False(target.IsFocused);
                Assert.Equal(trigger == UpdateSourceTrigger.Explicit ? "Initial" : "Edited", source.Name);
                if (trigger == UpdateSourceTrigger.Explicit)
                {
                    BindingOperations.GetBindingExpressionBase(target, TextBox.TextProperty)!.UpdateSource();
                    Assert.Equal("Edited", source.Name);
                }
                Assert.Equal(1, source.SetterCalls);
            });
        }
    }

    [AvaloniaFact]
    public void Unsupported_shapes_and_custom_culture_getters_do_not_fabricate_culture()
    {
        var unknown = new UntrustedCultureShape();
        var unknownDetails = new BindingDetailsReader().Read(unknown, new TextBlock());
        Assert.Equal(0, unknown.Calls);
        Assert.Equal(BindingDetailState.Unavailable, unknownDetails.ConverterCulture!.State);
        Assert.Equal(BindingDetailState.Unavailable, unknownDetails.ConverterCultureOrigin!.State);

        var culture = new CustomCulture();
        var target = new TextBlock();
        target.Bind(TextBlock.TextProperty, new Binding { Source = "Text", ConverterCulture = culture });
        WithWindow(target, () =>
        {
            var calls = culture.Calls;
            var details = Details(target);
            Assert.Equal(BindingDetailState.Unavailable, details.ConverterCulture!.State);
            Assert.Equal("Configured", details.ConverterCultureOrigin!.Value);
            Assert.Equal(calls, culture.Calls);
            var expression = BindingOperations.GetBindingExpressionBase(target, TextBlock.TextProperty)!;
            var disabled = new BindingDetailsReader(allowPrivateApi: false).Read(expression, target);
            Assert.Equal(BindingDetailState.Unavailable, disabled.ConverterCulture!.State);
            Assert.Contains(disabled.Gaps, gap => gap.Field == "ConverterCulture");
        });
    }

    [AvaloniaFact]
    public void Resource_expression_marks_converter_culture_not_applicable()
    {
        var target = new TextBlock { Resources = new ResourceDictionary { ["Key"] = "Value" } };
        target.Bind(TextBlock.TextProperty, new DynamicResourceExtension("Key"));
        WithWindow(target, () =>
        {
            var details = Details(target);
            Assert.Equal(BindingDetailState.NotApplicable, details.ConverterCulture!.State);
            Assert.Equal(BindingDetailState.NotApplicable, details.ConverterCultureOrigin!.State);
            Assert.Equal(BindingDetailState.NotApplicable, details.StringFormat.State);
        });
    }

    [Fact]
    public void Optional_culture_fields_round_trip_and_legacy_wire_payload_keeps_them_missing()
    {
        var value = new BindingDetailValue(BindingDetailState.Unknown);
        var details = new BindingDetails("binding", value, value, value, value, value, value, value, value, value,
            value, value, value, [], [], [], ConverterCulture: new(BindingDetailState.Known, "fr-FR"),
            ConverterCultureOrigin: new(BindingDetailState.Known, "Configured"));
        var json = System.Text.Json.JsonSerializer.Serialize(details, ProtocolJson.Options);
        var copy = System.Text.Json.JsonSerializer.Deserialize<BindingDetails>(json, ProtocolJson.Options)!;
        Assert.Equal("fr-FR", copy.ConverterCulture!.Value);
        Assert.Equal("Configured", copy.ConverterCultureOrigin!.Value);
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        legacy.Remove("converterCulture");
        legacy.Remove("converterCultureOrigin");
        var old = System.Text.Json.JsonSerializer.Deserialize<BindingDetails>(legacy.ToJsonString(), ProtocolJson.Options)!;
        Assert.Null(old.ConverterCulture);
        Assert.Null(old.ConverterCultureOrigin);
    }

    private static BindingDetails Details(AvaloniaObject target) => Assert.Single(
        Avalonia12DiagnosticsAdapter.Inspect(target).Bindings, binding => binding.PropertyName == "Text").Details!;

    private static void WithWindow(Control content, Action inspect)
    {
        var window = new Window { Content = content };
        window.Show();
        try { inspect(); }
        finally { window.Close(); }
    }

    private sealed class AmountModel
    {
        private decimal _amount = 1234.5m;
        public int GetterCalls { get; private set; }
        public decimal Amount { get { GetterCalls++; return _amount; } set => _amount = value; }
        public decimal Quantity => 2m;
        public override string ToString() => "Amount source";
    }

    private sealed class NameModel : INotifyPropertyChanged
    {
        private string _name = "Initial";
        public int GetterCalls { get; private set; }
        public int SetterCalls { get; private set; }
        public string Name
        {
            get { GetterCalls++; return _name; }
            set { _name = value; SetterCalls++; PropertyChanged?.Invoke(this, new(nameof(Name))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => "Name source";
    }

    private sealed class AmountConverter(bool formatInConverter = false) : IValueConverter
    {
        public string? ForwardCulture { get; private set; }
        public string? BackwardCulture { get; private set; }
        public int ForwardCalls { get; private set; }
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            ForwardCalls++;
            ForwardCulture = culture.Name;
            return formatInConverter ? ((decimal)value!).ToString("N2", culture) : value;
        }
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            BackwardCulture = culture.Name;
            return decimal.Parse((string)value!, NumberStyles.Number, culture);
        }
    }

    private sealed class SumConverter : IMultiValueConverter
    {
        public string? Culture { get; private set; }
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            Culture = culture.Name;
            return values.Sum(value => (decimal)value!);
        }
    }

    private sealed class UntrustedCultureShape
    {
        public int Calls { get; private set; }
        public CultureInfo ConverterCulture { get { Calls++; throw new InvalidOperationException(); } }
    }

    private sealed class CustomCulture() : CultureInfo("en-US")
    {
        public int Calls { get; private set; }
        public override string Name { get { Calls++; throw new InvalidOperationException("Custom culture getter"); } }
    }
}
