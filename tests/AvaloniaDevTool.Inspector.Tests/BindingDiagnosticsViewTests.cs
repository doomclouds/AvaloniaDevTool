using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Inspector.Tests;

public sealed class BindingDiagnosticsViewTests
{
    [Fact]
    public void Fields_distinguish_null_unknown_unavailable_and_weak_read_only_source()
    {
        var details = CreateDetails() with
        {
            Source = new(BindingDetailState.Known, "Customer", "Demo.Customer", 91),
            Mode = new(BindingDetailState.Unknown, Reason: "Runtime mode is unavailable."),
            Converter = new(BindingDetailState.Null),
            UpdateSourceTrigger = new(BindingDetailState.Unavailable, Reason: "Member is absent."),
            DataContext = new(BindingDetailState.Null),
            DataContextOrigin = new(BindingDetailState.Known, "Inherited"),
            CurrentValue = new(BindingDetailState.Known, "Rendered name", "System.String"),
            ConverterCulture = new(BindingDetailState.Known, "fr-FR", "System.Globalization.CultureInfo"),
            ConverterCultureOrigin = new(BindingDetailState.Known, "Configured", Reason: "Observed culture storage.")
        };
        var model = new BindingDetailsNodeViewModel(details);

        Assert.Equal(18, model.Fields.Count);
        var source = Assert.Single(model.Fields, row => row.Name == "来源对象");
        Assert.Equal("Customer", source.Value);
        Assert.Equal("已知", source.State);
        Assert.Equal("Demo.Customer", source.TypeName);
        Assert.Equal("对象 #91（只读引用）", source.ObjectReference);
        Assert.Contains(model.Fields, row => row.Name == "转换器" && row.Value == "null" && row.State == "空值");
        Assert.Contains(model.Fields, row => row.Name == "模式" && row.State == "未知" && row.Reason == "Runtime mode is unavailable.");
        Assert.Contains(model.Fields, row => row.Name == "更新源触发方式" && row.State == "不可用" && row.Reason == "Member is absent.");
        Assert.Contains(model.Fields, row => row.Name == "DataContext" && row.State == "空值");
        Assert.Contains(model.Fields, row => row.Name == "DataContext 来源" && row.Value == "Inherited");
        Assert.Contains(model.Fields, row => row.Name == "当前值" && row.Value == "Rendered name" && row.TypeName == "System.String");
        Assert.Contains(model.Fields, row => row.Name == "ConverterCulture" && row.Value == "fr-FR" && row.State == "已知");
        Assert.Contains(model.Fields, row => row.Name == "Culture 来源" && row.Value == "Configured" && row.Reason == "Observed culture storage.");
    }

    [AvaloniaFact]
    public void Axaml_renders_field_table_and_expandable_children_with_error_tree()
    {
        var details = CreateDetails() with
        {
            ConverterCulture = new(BindingDetailState.Known, "fr-FR"),
            ConverterCultureOrigin = new(BindingDetailState.Known, "Configured", Reason: "Observed parent storage."),
            Children = [CreateDetails("Child binding") with
            {
                ConverterCulture = new(BindingDetailState.Null, Reason: "No configured culture."),
                ConverterCultureOrigin = new(BindingDetailState.Known, "CurrentCulture fallback", Reason: "Not sampled.")
            }],
            Errors = [new("Converter", "Outer failure", "Demo.ConverterException",
                [new("InnerException", "Inner failure", "System.InvalidOperationException")])],
            Gaps = [new("Mode", "Private member unavailable.")]
        };
        var view = new BindingDiagnosticView
        {
            DataContext = new BindingDiagnosticInfo("Text", "Customer.Name", "ReflectionBinding", "ConverterError", null,
                "LocalValue", "Conversion failed", details)
        };
        var window = new Window { Width = 900, Height = 900, Content = view };
        window.Show();
        try
        {
            var metadata = FindItemsControl(view, "Inspector.BindingMetadata");
            Assert.Equal(4, metadata.Items.Count);
            var fields = FindItemsControl(view, "Inspector.BindingFields");
            Assert.Equal(18, fields.Items.Count);
            Assert.Contains(fields.Items.OfType<BindingDetailRowViewModel>(), row => row.Name == "ConverterCulture" && row.Value == "fr-FR");
            Assert.All(fields.Items.OfType<BindingDetailRowViewModel>(), row => Assert.False(string.IsNullOrEmpty(row.State)));
            var errors = Assert.IsType<TreeView>(FindItemsControl(view, "Inspector.BindingErrors"));
            var error = Assert.IsType<BindingErrorNodeViewModel>(Assert.Single(errors.Items));
            Assert.Equal("Inner failure", Assert.Single(error.InnerErrors).Message);
            var children = Assert.Single(view.GetVisualDescendants().OfType<Expander>(),
                expander => AutomationProperties.GetAutomationId(expander) == "Inspector.BindingChildren");
            Assert.False(children.IsExpanded);
            Assert.Equal("子表达式（1）", children.Header);
            children.IsExpanded = true;
            Assert.True(children.IsExpanded);
            window.UpdateLayout();
            var tables = view.GetVisualDescendants().OfType<ItemsControl>()
                .Where(control => AutomationProperties.GetAutomationId(control) == "Inspector.BindingFields").ToArray();
            Assert.Equal(2, tables.Length);
            Assert.Contains(tables, table => table.Items.OfType<BindingDetailRowViewModel>()
                .Any(row => row.Name == "Culture 来源" && row.Value == "CurrentCulture fallback"));
            var card = Assert.IsType<BindingDiagnosticCardViewModel>(view.FindControl<StackPanel>("BindingContent")!.DataContext);
            Assert.Equal("未知", Assert.Single(card.Metadata, row => row.Name == "是否运行").State);
            Assert.Equal("Child binding", Assert.Single(card.Details!.Children).Kind);
            Assert.Contains(Assert.Single(card.Details.Children).Fields, row => row.Name == "ConverterCulture" && row.State == "空值");
            Assert.Contains(Assert.Single(card.Details.Children).Fields, row => row.Name == "Culture 来源" && row.Value == "CurrentCulture fallback" && row.Reason == "Not sampled.");
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Legacy_binding_keeps_explicit_unavailable_notice_without_fabricating_details()
    {
        var view = new BindingDiagnosticView
        {
            DataContext = new BindingDiagnosticInfo("Text", "Name", "LegacyBinding", "None", true, "Unknown", null)
        };
        var model = Assert.IsType<BindingDiagnosticCardViewModel>(view.FindControl<StackPanel>("BindingContent")!.DataContext);
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        try
        {
            Assert.Null(model.Details);
            Assert.True(model.HasNoDetails);
            Assert.Equal("未知", Assert.Single(model.Metadata, row => row.Name == "优先级").State);
            Assert.Equal("是", Assert.Single(model.Metadata, row => row.Name == "是否运行").Value);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), block =>
                block.Text == "当前后端未提供结构化绑定诊断。" && block.IsVisible);
        }
        finally { window.Close(); }
    }

    [Fact]
    public void Child_and_error_presentation_are_bounded_and_show_truncation()
    {
        var details = CreateDetails() with
        {
            Children = Enumerable.Range(0, 65).Select(_ => CreateDetails("Child")).ToArray(),
            Errors = [new("Binding", "Failure", InnerErrors: null)]
        };
        var model = new BindingDetailsNodeViewModel(details);

        Assert.Equal(64, model.Children.Count);
        Assert.True(model.Truncated);
        Assert.Empty(Assert.Single(model.Errors).InnerErrors);
        Assert.Contains(model.Fields, row => row.Name == "DataContext" && row.State == "不可用" && row.HasReason);
        Assert.Contains(model.Fields, row => row.Name == "ConverterCulture" && row.State == "不可用" && row.HasReason);
    }

    [Theory]
    [InlineData(BindingDetailState.Known, "已知")]
    [InlineData(BindingDetailState.Null, "空值")]
    [InlineData(BindingDetailState.Unknown, "未知")]
    [InlineData(BindingDetailState.Unavailable, "不可用")]
    [InlineData(BindingDetailState.NotApplicable, "不适用")]
    public void Culture_field_state_is_not_reinterpreted(BindingDetailState state, string label)
    {
        var model = new BindingDetailsNodeViewModel(CreateDetails() with { ConverterCulture = new(state, "en-US", Reason: "Evidence") });
        var row = Assert.Single(model.Fields, field => field.Name == "ConverterCulture");
        Assert.Equal(label, row.State);
        Assert.Equal("Evidence", row.Reason);
        Assert.Equal(state == BindingDetailState.Known ? "en-US" : state == BindingDetailState.Null ? "null" : label, row.Value);
    }

    private static ItemsControl FindItemsControl(Control root, string id) =>
        Assert.Single(root.GetVisualDescendants().OfType<ItemsControl>(), control => AutomationProperties.GetAutomationId(control) == id);

    private static BindingDetails CreateDetails(string kind = "ReflectionBinding")
    {
        var known = new BindingDetailValue(BindingDetailState.Known, "Value");
        return new BindingDetails(kind, known, known, known, known, known, known, known, known, known, known, known,
            known, [], [], []);
    }
}
