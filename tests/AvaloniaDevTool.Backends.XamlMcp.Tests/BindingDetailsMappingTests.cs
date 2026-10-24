using AvaloniaDevTool.Protocol;
using System.Text.Json;
using Upstream = global::XamlMcp.Protocol;
using Xunit;

namespace AvaloniaDevTool.Backends.XamlMcp.Tests;

public sealed class BindingDetailsMappingTests
{
    [Fact]
    public void Known_values_and_observed_null_preserve_types_without_native_object_handles()
    {
        var binding = Binding() with
        {
            Source = new(Upstream.ValueKind.Complex, null, "Demo.Model", "Model summary", false),
            SourceType = "Demo.Model",
            Path = "Name",
            Mode = "TwoWay",
            ConverterType = "Demo.Converter",
            Status = "active",
            CurrentValue = new(Upstream.ValueKind.String, "Rendered name", "System.String", null, false)
        };
        var mapped = XamlBindingDetailsMapper.Map(Result(binding));
        var row = Assert.Single(mapped.Bindings);
        var details = Assert.IsType<BindingDetails>(row.Details);

        Assert.True(row.IsRunning);
        Assert.Equal("Unknown", row.Priority);
        Assert.Equal(BindingDetailState.Known, details.Source.State);
        Assert.Equal("Model summary", details.Source.Value);
        Assert.Equal("Demo.Model", details.Source.TypeName);
        Assert.Null(details.Source.ObjectId);
        Assert.Equal("Name", details.Path.Value);
        Assert.Equal("TwoWay", details.Mode.Value);
        Assert.Equal("Demo.Converter", details.Converter.Value);
        Assert.Equal(BindingDetailState.Known, details.CurrentValue!.State);
        Assert.Equal("Rendered name", details.CurrentValue.Value);
        Assert.Equal("System.String", details.CurrentValue.TypeName);
        Assert.Null(details.CurrentValue.ObjectId);
        Assert.Equal(BindingDetailState.Known, details.DataContext!.State);
        Assert.Equal("Demo.Context", details.DataContext.Value);
        Assert.Null(details.DataContext.ObjectId);
        Assert.Equal("inherited", details.DataContextOrigin!.Value);
        Assert.Contains(details.Gaps, gap => gap.Field == "DataContext.Value");
        Assert.Equal(BindingDetailState.Unavailable, details.ResourceKey!.State);
        Assert.Null(details.ResourceKey.ObjectId);
        Assert.Contains(details.Gaps, gap => gap.Field == "ResourceKey");

        var nullSource = Assert.Single(XamlBindingDetailsMapper.Map(Result(binding with
        {
            Source = new(Upstream.ValueKind.Null, null, null, null, false), ConverterType = null,
            CurrentValue = new(Upstream.ValueKind.Null, null, null, null, false)
        })).Bindings).Details!;
        Assert.Equal(BindingDetailState.Null, nullSource.Source.State);
        Assert.Equal(BindingDetailState.Null, nullSource.Converter.State);
        Assert.Equal(BindingDetailState.Null, nullSource.CurrentValue!.State);
    }

    [Fact]
    public void Missing_fields_distinguish_unknown_from_explicit_upstream_gaps()
    {
        var unknown = Assert.Single(XamlBindingDetailsMapper.Map(Result(Binding())).Bindings).Details!;
        Assert.Equal(BindingDetailState.Unknown, unknown.Source.State);
        Assert.Equal(BindingDetailState.Unknown, unknown.SourceType.State);
        Assert.Equal(BindingDetailState.Unknown, unknown.Path.State);
        Assert.Equal(BindingDetailState.Unknown, unknown.Mode.State);
        Assert.Equal(BindingDetailState.Null, unknown.Converter.State);
        Assert.Contains(unknown.Gaps, gap => gap.Field == "Source");
        Assert.Equal(BindingDetailState.Unavailable, unknown.ConverterParameter.State);
        Assert.Equal(BindingDetailState.Unavailable, unknown.ConverterCulture!.State);
        Assert.Equal(BindingDetailState.Unavailable, unknown.ConverterCultureOrigin!.State);
        Assert.Contains(unknown.Gaps, gap => gap.Field == "ConverterCulture" && gap.Reason.Contains("not inferred"));
        Assert.Equal(BindingDetailState.Unavailable, unknown.FallbackValue.State);
        Assert.Equal(BindingDetailState.Unavailable, unknown.TargetNullValue.State);
        Assert.Equal(BindingDetailState.Unavailable, unknown.UpdateSourceTrigger.State);
        Assert.Equal(BindingDetailState.Unavailable, unknown.StringFormat.State);
        Assert.Equal(BindingDetailState.Unavailable, unknown.ElementName.State);
        Assert.Equal(BindingDetailState.Unavailable, unknown.RelativeSource.State);
        var gaps = new[]
        {
            new Upstream.DiagnosticGapDto("bindings.source", "Composite source unavailable."),
            new Upstream.DiagnosticGapDto("bindings.path", "Composite path unavailable."),
            new Upstream.DiagnosticGapDto("bindings.mode", "Effective mode unavailable."),
            new Upstream.DiagnosticGapDto("bindings.converter", "Converter unavailable.")
        };
        var unavailable = Assert.Single(XamlBindingDetailsMapper.Map(Result(Binding() with { Gaps = gaps })).Bindings).Details!;
        Assert.Equal(BindingDetailState.Unavailable, unavailable.Source.State);
        Assert.Equal("Composite source unavailable.", unavailable.Source.Reason);
        Assert.Equal(BindingDetailState.Unavailable, unavailable.Path.State);
        Assert.Equal(BindingDetailState.Unavailable, unavailable.Mode.State);
        Assert.Equal(BindingDetailState.Unavailable, unavailable.Converter.State);
        Assert.Contains(unavailable.Gaps, gap => gap.Field == "bindings.mode" && gap.Reason == "Effective mode unavailable.");
    }

    [Fact]
    public void Independent_source_type_gap_is_preserved_without_claiming_source_is_unavailable()
    {
        var details = Assert.Single(XamlBindingDetailsMapper.Map(Result(Binding() with
        {
            Gaps = [new("bindings.sourceType", "Source type is unavailable.")]
        })).Bindings).Details!;
        Assert.Equal(BindingDetailState.Unavailable, details.SourceType.State);
        Assert.Equal("Source type is unavailable.", details.SourceType.Reason);
        Assert.Equal(BindingDetailState.Unknown, details.Source.State);
        Assert.Contains(details.Gaps, gap => gap.Field == "bindings.sourceType");
    }

    [Fact]
    public void Child_tree_errors_and_upstream_gaps_remain_structured_without_duplicate_top_level_rows()
    {
        var child = Binding() with
        {
            Kind = "binding", Path = "Child.Name", Status = "validation-error",
            Errors = [new("validation-error", "Child validation failed.")],
            Gaps = [new("bindings.errors.raw", "Raw exception is not exposed.")]
        };
        var parent = Binding() with { Kind = "multi-binding", Children = [child], Status = "converter-error",
            Errors = [new("converter-error", "Conversion failed.")] };
        var mapped = XamlBindingDetailsMapper.Map(Result(parent));
        var row = Assert.Single(mapped.Bindings);
        var details = row.Details!;

        Assert.Null(row.IsRunning);
        Assert.Equal("converter-error", row.ErrorType);
        var error = Assert.Single(details.Errors);
        Assert.Equal("converter-error", error.Category);
        Assert.Equal("Conversion failed.", error.Message);
        Assert.Null(error.ExceptionType);
        Assert.Null(error.InnerErrors);
        Assert.Contains(details.Gaps, gap => gap.Field == "Errors.Exception");
        var nested = Assert.Single(details.Children);
        Assert.Equal("Child.Name", nested.Path.Value);
        Assert.Equal("validation-error", Assert.Single(nested.Errors).Category);
        Assert.Contains(nested.Gaps, gap => gap.Field == "bindings.errors.raw");
        Assert.Equal("Child validation failed.", Assert.Single(mapped.ValidationErrors));
        Assert.Contains(mapped.Limitations, gap => gap.Contains("Raw exception is not exposed."));
    }

    [Theory]
    [InlineData("active", true)]
    [InlineData("inactive", false)]
    [InlineData("path-error", null)]
    [InlineData("validation-error", null)]
    [InlineData("converter-error", null)]
    public void Only_activity_status_can_determine_running_state(string status, bool? running)
    {
        var row = Assert.Single(XamlBindingDetailsMapper.Map(Result(Binding() with { Status = status })).Bindings);
        Assert.Equal(running, row.IsRunning);
        Assert.Equal(BindingDetailState.Unknown, row.Details!.Mode.State);
    }

    [Fact]
    public void Unknown_data_context_and_foreign_supplier_ids_do_not_fabricate_native_references()
    {
        var result = Result(Binding()) with { DataContext = new("unknown", null, new(123, 456)) };
        var details = Assert.Single(XamlBindingDetailsMapper.Map(result).Bindings).Details!;
        Assert.Equal(BindingDetailState.Unknown, details.DataContext!.State);
        Assert.Equal(BindingDetailState.Unknown, details.DataContextOrigin!.State);
        Assert.Null(details.DataContext.ObjectId);
        Assert.Null(details.DataContextOrigin.ObjectId);
        var observedNull = Assert.Single(XamlBindingDetailsMapper.Map(result with
        {
            DataContext = new("local", null, null)
        }).Bindings).Details!;
        Assert.Equal(BindingDetailState.Null, observedNull.DataContext!.State);
        Assert.Equal(BindingDetailState.Known, observedNull.DataContextOrigin!.State);
    }

    [Fact]
    public void Text_children_depth_and_total_node_limits_produce_visible_truncation()
    {
        var longText = new string('x', 254) + "😀" + "tail";
        var leaf = Binding() with
        {
            Path = longText, Source = new(Upstream.ValueKind.String, longText, "System.String", null, true),
            CurrentValue = new(Upstream.ValueKind.Complex, null, "Demo.Value", "Value summary", true)
        };
        var branch = Binding() with { Children = Enumerable.Repeat(leaf, 40).ToArray() };
        var mapped = XamlBindingDetailsMapper.Map(Result(branch, branch, branch, branch, branch));
        Assert.True(mapped.Truncated);
        Assert.True(mapped.Bindings.Sum(row => Count(row.Details!)) <= 128);
        var first = mapped.Bindings[0].Details!;
        Assert.Equal(32, first.Children.Count);
        Assert.True(first.Truncated);
        Assert.Contains(first.Gaps, gap => gap.Field == "Children");
        var text = first.Children[0].Path.Value!;
        Assert.True(text.Length <= 256);
        Assert.False(char.IsHighSurrogate(text[^2]));
        Assert.Contains(first.Children[0].Gaps, gap => gap.Field == "Source");
        Assert.Equal(BindingDetailState.Known, first.Children[0].CurrentValue!.State);
        Assert.Equal("Value summary", first.Children[0].CurrentValue!.Value);
        Assert.Equal("Demo.Value", first.Children[0].CurrentValue!.TypeName);
        Assert.Null(first.Children[0].CurrentValue!.ObjectId);
        Assert.Contains(first.Children[0].Gaps, gap => gap.Field == "CurrentValue");
        Assert.True(first.Children[0].Truncated);
        var deep = leaf;
        for (var depth = 0; depth < 10; depth++) deep = Binding() with { Children = [deep] };
        var deepResult = Assert.Single(XamlBindingDetailsMapper.Map(Result(deep)).Bindings).Details!;
        Assert.Equal(7, Count(deepResult));
        Assert.True(deepResult.Truncated);
    }

    [Fact]
    public void Global_gaps_survive_even_without_binding_rows()
    {
        var mapped = XamlBindingDetailsMapper.Map(Result() with { Gaps = [new("bindings", "Binding rows were truncated upstream.")] });
        Assert.Empty(mapped.Bindings);
        Assert.True(mapped.Truncated);
        Assert.Contains(mapped.Limitations, value => value == "bindings: Binding rows were truncated upstream.");
    }

    [Fact]
    public void Whole_tree_text_budget_keeps_adversarial_escaped_json_below_protocol_limit()
    {
        var text = new string('坏', 400);
        var gaps = Enumerable.Range(0, 32).Select(index => new Upstream.DiagnosticGapDto(index + text, text)).ToArray();
        var leaf = Binding() with
        {
            TargetProperty = text, Path = text, SourceType = text, Mode = text, ConverterType = text,
            Source = new(Upstream.ValueKind.Complex, text, text, text, false), Gaps = gaps,
            Errors = Enumerable.Range(0, 32).Select(_ => new Upstream.BindingErrorDto(text, text)).ToArray()
        };
        var branch = leaf with { Children = Enumerable.Repeat(leaf, 32).ToArray() };
        var mapped = XamlBindingDetailsMapper.Map(Result(branch, branch, branch, branch, branch) with
        {
            Gaps = gaps, DataContext = new(text, text, new(123, 456))
        });
        var snapshot = new StyleSnapshot([], mapped.Bindings, mapped.ValidationErrors, "XamlMcp", mapped.Truncated, mapped.Limitations);
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, ProtocolJson.Options);

        Assert.True(mapped.Truncated);
        Assert.True(json.Length < RpcPeer.MaximumFrameBytes, $"Mapped response contains {json.Length} bytes.");
        Assert.Contains(mapped.Bindings.SelectMany(row => Descendants(row.Details!)), details => details.Path.Value == "…");
    }

    private static int Count(BindingDetails details) => 1 + details.Children.Sum(Count);

    private static IEnumerable<BindingDetails> Descendants(BindingDetails details)
    {
        yield return details;
        foreach (var child in details.Children)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static Upstream.BindingDiagnosticDto Binding() => new("binding", "Text", null, null, null, null,
        null, null, new(Upstream.ValueKind.Null, null, null, null, false), "active", [], [], []);

    private static Upstream.BindingsResult Result(params Upstream.BindingDiagnosticDto[] bindings) =>
        new(bindings, new("inherited", "Demo.Context", new(123, 456)), new(null, []), false, []);
}
