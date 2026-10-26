using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
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

public sealed class BindingDetailsTests
{
    [AvaloniaFact]
    public void Dynamic_resource_reports_actual_key_host_and_explicit_private_shape_gaps()
    {
        var target = new TextBlock { Resources = new ResourceDictionary { ["RealKey"] = "Resource value" } };
        target.Bind(TextBlock.TextProperty, new DynamicResourceExtension("RealKey"));
        WithWindow(target,
            () =>
            {
                var objects = new WeakObjectRegistry();
                var details = TextBinding(target, objects.GetId).Details!;
                Assert.Equal("dynamic-resource", details.Kind);
                Assert.Equal("RealKey", details.ResourceKey!.Value);
                Assert.Equal(typeof(string).FullName, details.ResourceKey.TypeName);
                Assert.Null(details.ResourceKey.ObjectId);
                Assert.Same(target, objects.Get(details.Source.ObjectId!.Value));
                Assert.Equal("Resource value", details.CurrentValue!.Value);
                Assert.Equal(BindingDetailState.Unavailable, details.Path.State);
                var expression = BindingOperations.GetBindingExpressionBase(target, TextBlock.TextProperty)!;
                var stripped = new BindingDetailsReader(objects.GetId, allowPrivateApi: false).Read(expression, target);
                Assert.Equal(BindingDetailState.Unavailable, stripped.ResourceKey!.State);
                Assert.Contains(stripped.Gaps, gap => gap.Field == "ResourceKey");
                Assert.Null(stripped.ResourceKey.ObjectId);
                Assert.Equal(BindingDetailState.Unavailable, stripped.Source.State);
            });
    }

    [AvaloniaFact]
    public void Resource_key_budget_never_turns_a_truncated_preview_into_a_string_identity()
    {
        var key = new string('k', 300);
        var target = new TextBlock { Resources = new ResourceDictionary { [key] = "Value" } };
        target.Bind(TextBlock.TextProperty, new DynamicResourceExtension(key));
        WithWindow(target,
            () =>
            {
                var details = TextBinding(target).Details!;
                Assert.Equal(BindingDetailState.Unavailable, details.ResourceKey!.State);
                Assert.Null(details.ResourceKey.Value);
                Assert.Null(details.ResourceKey.ObjectId);
                Assert.Contains(details.Gaps,
                    gap => gap.Field == "ResourceKey" && gap.Reason.Contains("preview cannot identify"));
                Assert.True(details.Truncated);
            });
    }

    [AvaloniaFact]
    public void Healthy_binding_reports_effective_mode_options_and_inherited_data_context()
    {
        var model = new Person("Ada");
        var target = new TextBox();
        target.Bind(TextBox.TextProperty,
            new Binding(nameof(Person.Name))
            {
                Mode = BindingMode.TwoWay, Converter = new EchoConverter(), ConverterParameter = "parameter",
                FallbackValue = "fallback", TargetNullValue = "empty",
                UpdateSourceTrigger = UpdateSourceTrigger.Explicit,
                StringFormat = "{0}"
            });
        WithWindow(new StackPanel { DataContext = model, Children = { target } },
            () =>
            {
                Assert.Equal("Ada", target.Text);
                var objects = new WeakObjectRegistry();
                var binding = TextBinding(target, objects.GetId);
                var details = Assert.IsType<BindingDetails>(binding.Details);
                Assert.Equal("binding", details.Kind);
                Assert.Equal(nameof(Person.Name), details.Path.Value);
                Assert.Equal("TwoWay", details.Mode.Value);
                Assert.Equal("Explicit", details.UpdateSourceTrigger.Value);
                Assert.Equal("Ada", details.CurrentValue!.Value);
                Assert.Equal("parameter", details.ConverterParameter.Value);
                Assert.Equal("fallback", details.FallbackValue.Value);
                Assert.Equal("empty", details.TargetNullValue.Value);
                Assert.Equal(typeof(Person).FullName, details.SourceType.Value);
                Assert.Same(model, objects.Get(details.Source.ObjectId!.Value));
                Assert.Same(model, objects.Get(details.DataContext!.ObjectId!.Value));
                Assert.Equal("Inherited", details.DataContextOrigin!.Value);
                Assert.Empty(details.Errors);
                Assert.True(binding.IsRunning);
                target.Text = "Pending";
                Assert.Equal("Ada", model.Name);
            });
    }

    [AvaloniaFact]
    public void Missing_data_context_and_bad_path_have_distinct_evidence_and_live_activity()
    {
        var missing = Bound(new Binding("Name"));
        var badPath = Bound(new Binding("Missing") { Source = new Person("Ada") });
        WithWindow(new StackPanel { Children = { missing, badPath } },
            () =>
            {
                var missingBinding = TextBinding(missing);
                var badBinding = TextBinding(badPath);
                Assert.Equal(BindingDetailState.Null, missingBinding.Details!.Source.State);
                Assert.Equal("missing-source", Assert.Single(missingBinding.Details.Errors).Category);
                Assert.Equal("path-error", Assert.Single(badBinding.Details!.Errors).Category);
                Assert.Contains("BindingChainException", Assert.Single(badBinding.Details.Errors).ExceptionType);
                Assert.True(missingBinding.IsRunning);
                Assert.True(badBinding.IsRunning);
                Assert.Equal("OneWay", badBinding.Details.Mode.Value);
            });
    }

    [AvaloniaFact]
    public void Converter_failure_retains_inner_exception_and_does_not_invent_a_path_failure()
    {
        var target = Bound(new Binding("Name") { Source = new Person("Ada"), Converter = new ThrowingConverter() });
        WithWindow(target,
            () =>
            {
                var error = Assert.Single(TextBinding(target).Details!.Errors);
                Assert.Equal("converter-error", error.Category);
                Assert.Equal(typeof(InvalidCastException).FullName, error.ExceptionType);
                var inner = Assert.Single(error.InnerErrors!);
                Assert.Equal(typeof(InvalidOperationException).FullName, inner.ExceptionType);
                Assert.Equal("converter failure", inner.Message);
            });
    }

    [AvaloniaFact]
    public void Validation_failure_keeps_validation_category_and_actual_exception()
    {
        var target = new TextBox();
        target.Bind(TextBox.TextProperty, new Binding("Name") { Source = new ValidatedPerson() });
        WithWindow(target,
            () =>
            {
                var details = TextBinding(target).Details!;
                Assert.Equal("validation-error", Assert.Single(details.Errors).Category);
                Assert.Contains("Name is invalid", Assert.Single(details.Errors).Message);
            });
    }

    [AvaloniaFact]
    public void Template_binding_reports_parent_property_and_retained_mode()
    {
        var button = new Button
        {
            Content = "Template text",
            Template = new FuncControlTemplate<Button>((_, _) =>
            {
                var text = new TextBlock();
                text.Bind(TextBlock.TextProperty,
                    new TemplateBinding(ContentControl.ContentProperty) { Mode = BindingMode.OneWay });
                return text;
            })
        };
        WithWindow(button,
            () =>
            {
                button.ApplyTemplate();
                var target = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>());
                Assert.Equal("Template text", target.Text);
                var objects = new WeakObjectRegistry();
                var details = TextBinding(target, objects.GetId).Details!;
                Assert.Equal("template-binding", details.Kind);
                Assert.Equal("Content", details.Path.Value);
                Assert.Equal("OneWay", details.Mode.Value);
                Assert.Equal("TemplatedParent", details.RelativeSource.Value);
                Assert.Same(button, objects.Get(details.Source.ObjectId!.Value));
                Assert.Equal(BindingDetailState.Unavailable, details.FallbackValue.State);
                Assert.Equal(BindingDetailState.Null, details.ConverterCulture!.State);
                Assert.Equal("CurrentCulture fallback", details.ConverterCultureOrigin!.Value);
                Assert.Equal(BindingDetailState.NotApplicable, details.StringFormat.State);
            });
    }

    [AvaloniaFact]
    public void Multi_binding_keeps_child_expression_tree_and_child_failures()
    {
        var model = new Person("Ada");
        var target = new TextBlock();
        target.Bind(TextBlock.TextProperty,
            new MultiBinding
            {
                Converter = new JoinConverter(), ConverterParameter = "multi-parameter", FallbackValue = "fallback",
                Bindings = { new Binding("Name") { Source = model }, new Binding("Missing") { Source = model } }
            });
        WithWindow(target,
            () =>
            {
                var details = TextBinding(target).Details!;
                Assert.Equal("multi-binding", details.Kind);
                Assert.Equal(BindingDetailState.Unavailable, details.Source.State);
                Assert.Equal(BindingDetailState.Unavailable, details.Path.State);
                Assert.Equal(BindingDetailState.Unavailable, details.Mode.State);
                Assert.Equal("multi-parameter", details.ConverterParameter.Value);
                Assert.Equal(BindingDetailState.Null, details.ConverterCulture!.State);
                Assert.Equal("CurrentCulture fallback", details.ConverterCultureOrigin!.Value);
                Assert.Equal(BindingDetailState.Null, details.StringFormat.State);
                Assert.Equal(2, details.Children.Count);
                Assert.Equal("Name", details.Children[0].Path.Value);
                Assert.Empty(details.Children[0].Errors);
                Assert.Equal("path-error", Assert.Single(details.Children[1].Errors).Category);
            });
    }

    [AvaloniaFact]
    public void Private_shape_can_be_disabled_and_missing_privateapi_members_degrade_per_field()
    {
        var target = Bound(new Binding("Name") { Source = new Person("Ada"), Mode = BindingMode.OneTime });
        WithWindow(target,
            () =>
            {
                var expression = BindingOperations.GetBindingExpressionBase(target, TextBlock.TextProperty)!;
                var details = new BindingDetailsReader(allowPrivateApi: false).Read(expression, target);
                Assert.Equal(BindingDetailState.Unavailable, details.Mode.State);
                Assert.Contains(details.Gaps, gap => gap.Field == "Mode");
                Assert.Equal("Name", details.Path.Value);
                var stripped = new BindingDetailsReader().Read(new ReferenceAssemblyShape(), target);
                Assert.Equal("unknown", stripped.Kind);
                Assert.Equal(BindingDetailState.Unavailable, stripped.Mode.State);
                Assert.Equal(BindingDetailState.Unavailable, stripped.Path.State);
                Assert.Contains(stripped.Gaps, gap => gap.Field == "Errors");
                Assert.Empty(stripped.Errors);
            });
    }

    [AvaloniaFact]
    public void Throwing_getters_and_display_are_isolated_without_losing_other_fields()
    {
        var details = new BindingDetailsReader().Read(new ThrowingDiagnosticShape(), new TextBlock());
        Assert.Equal(BindingDetailState.Unavailable, details.Path.State);
        Assert.Equal(BindingDetailState.Unavailable, details.Source.State);
        Assert.Equal(BindingDetailState.Unavailable, details.FallbackValue.State);
        Assert.Equal(BindingDetailState.Null, details.Converter.State);
        Assert.Equal("parameter", details.ConverterParameter.Value);
        Assert.Contains(details.Gaps, gap => gap.Field == "Source" && gap.Reason.Contains("Display getter failed"));
        Assert.Contains(details.Gaps,
            gap => gap.Field == "FallbackValue" && gap.Reason.Contains("Runtime getter failed"));
    }

    [Fact]
    public void Exception_depth_count_text_and_surrogate_boundaries_are_bounded()
    {
        Exception error = new InvalidOperationException(new string('x', 255) + "😀tail");
        for (var i = 0; i < 10; ++i)
            error = new AggregateException("Outer", Enumerable.Repeat(error, 20));
        var reader = new BindingDetailsReader();
        var result = reader.DescribeError(error, "binding-error");
        Assert.True(reader.Truncated);
        Assert.True(result.Truncated);
        Assert.True(CountErrors(result) <= 16);
        Assert.True(ErrorDepth(result) <= 5);
        var text = reader.Text(new string('x', 254) + "😀tail")!;
        Assert.True(text.Length <= BindingDetailsReader.MaxTextLength);
        Assert.False(char.IsHighSurrogate(text[^2]));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result).Length < 32768);
    }

    [AvaloniaFact]
    public void Source_registration_and_snapshot_do_not_keep_business_objects_alive()
    {
        var objects = new WeakObjectRegistry();
        var (source, snapshot) = ReadTemporarySource(objects);
        for (var i = 0; i < 4 && source.IsAlive; ++i)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(source.IsAlive);
        Assert.False(objects.TryGet(snapshot.Source.ObjectId!.Value, out _));
        GC.KeepAlive(snapshot);
    }

    [AvaloniaFact]
    public void Composite_children_and_serialized_response_stay_bounded()
    {
        var target = new TextBlock();
        var binding = new MultiBinding { Converter = new JoinConverter() };
        for (var i = 0; i < 100; ++i)
            binding.Bindings.Add(new Binding("Name") { Source = new Person(new string('名', 1000)) });
        target.Bind(TextBlock.TextProperty, binding);
        WithWindow(target,
            () =>
            {
                var snapshot = Avalonia12DiagnosticsAdapter.Inspect(target);
                var details = Assert.Single(snapshot.Bindings, value => value.PropertyName == "Text").Details!;
                Assert.Equal(32, details.Children.Count);
                Assert.True(details.Truncated);
                Assert.True(snapshot.Truncated);
                Assert.True(JsonSerializer.SerializeToUtf8Bytes(snapshot).Length < 4 * 1024 * 1024);
            });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Source, BindingDetails Snapshot) ReadTemporarySource(WeakObjectRegistry objects)
    {
        var model = new Person("temporary");
        var target = Bound(new Binding("Name") { Source = model });
        BindingDetails? details = null;
        WithWindow(target, () => details = TextBinding(target, objects.GetId).Details);
        return (new WeakReference(model), details!);
    }

    private static int CountErrors(BindingErrorInfo error) => 1 + (error.InnerErrors ?? []).Sum(CountErrors);

    private static int ErrorDepth(BindingErrorInfo error) =>
        1 + ((error.InnerErrors ?? []).Count == 0 ? 0 : error.InnerErrors!.Max(ErrorDepth));

    private static TextBlock Bound(Binding binding)
    {
        var target = new TextBlock();
        target.Bind(TextBlock.TextProperty, binding);
        return target;
    }

    private static BindingDiagnosticInfo TextBinding(AvaloniaObject target, Func<object, long>? objects = null) =>
        Assert.Single(Avalonia12DiagnosticsAdapter.Inspect(target, objects).Bindings,
            binding => binding.PropertyName == "Text");

    private static void WithWindow(Control content, Action inspect)
    {
        var window = new Window { Content = content };
        window.Show();
        try
        {
            inspect();
        }
        finally
        {
            window.Close();
        }
    }

    private sealed record Person(string Name);

    private sealed class EchoConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value;
    }

    private sealed class ThrowingConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new InvalidOperationException("converter failure");

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    private sealed class JoinConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
            string.Join("/", values.Select(value => value?.ToString()));
    }

    private sealed class ValidatedPerson : INotifyDataErrorInfo
    {
        public string Name => "invalid";
        public bool HasErrors => true;

        public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged
        {
            add { }
            remove { }
        }

        public IEnumerable GetErrors(string? propertyName) => new[] { "Name is invalid" };
    }

    private sealed class ReferenceAssemblyShape;

    private sealed class ThrowingDiagnosticShape
    {
        public string Description => throw new InvalidOperationException("description unavailable");
        public object LeafNode => new ThrowingDisplayLeaf();
        public object? Converter => null;
        public string ConverterParameter => "parameter";
        public object FallbackValue => throw new InvalidOperationException("fallback unavailable");
        public string ErrorType => "None";
    }

    private sealed class ThrowingDisplayLeaf
    {
        public object Source => new ThrowingDisplay();
        public object? Value => null;
    }

    private sealed class ThrowingDisplay
    {
        public override string ToString() => throw new InvalidOperationException("display unavailable");
    }
}
