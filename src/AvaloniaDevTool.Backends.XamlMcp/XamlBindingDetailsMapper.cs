using AvaloniaDevTool.Protocol;
using Upstream = global::XamlMcp.Protocol;

namespace AvaloniaDevTool.Backends.XamlMcp;

internal sealed record XamlBindingMapping(IReadOnlyList<BindingDiagnosticInfo> Bindings,
    IReadOnlyList<string> ValidationErrors, IReadOnlyList<string> Limitations, bool Truncated);

/// <summary>Maps upstream wire evidence without treating snapshot node references as native object IDs.</summary>
internal static class XamlBindingDetailsMapper
{
    private const int MaximumNodes = 128;
    private const int MaximumChildren = 32;
    private const int MaximumDepth = 6;
    private const int MaximumErrors = 32;
    private const int MaximumUpstreamGaps = 32;
    private const int MaximumTextUnits = 32_768;

    internal static XamlBindingMapping Map(Upstream.BindingsResult result)
    {
        var context = new MappingContext(result.DataContext, result.Gaps);
        context.Limitations.AddRange(result.Gaps.Take(MaximumUpstreamGaps).Select(gap => context.Bound(gap.Field + ": " + gap.Reason)!));
        if (result.Gaps.Count > MaximumUpstreamGaps || result.Gaps.Any(gap => gap.Reason.Contains("truncat", StringComparison.OrdinalIgnoreCase))) context.Truncate();
        var bindings = new List<BindingDiagnosticInfo>();
        foreach (var binding in result.Bindings)
        {
            if (context.RemainingNodes == 0) { context.Truncate(); break; }
            var details = MapDetails(binding, context, 0);
            var errors = details.Errors;
            var expression = binding.FrameworkDescription ?? $"Path={binding.Path ?? "(none)"}; Source={binding.SourceType ?? "unknown"}; Mode={binding.Mode ?? "unknown"}";
            bindings.Add(new BindingDiagnosticInfo(context.Bound(binding.TargetProperty)!, context.Bound(expression)!,
                context.Bound("XamlMcp/" + binding.Kind)!,
                errors.Count == 0 ? "None" : context.Bound(string.Join(", ", errors.Select(error => error.Category).Distinct()))!,
                binding.Status == "active" ? true : binding.Status == "inactive" ? false : null,
                "Unknown", errors.Count == 0 ? null : context.Bound(string.Join("; ", errors.Select(error => error.Category + ": " + error.Message))),
                details));
        }
        return new XamlBindingMapping(bindings, context.Validation.Distinct().Take(MaximumNodes).ToArray(),
            context.Limitations.Distinct().Take(64).ToArray(), context.TruncationCount > 0);
    }

    private static BindingDetails MapDetails(Upstream.BindingDiagnosticDto binding, MappingContext context, int depth)
    {
        context.RemainingNodes--;
        var before = context.TruncationCount;
        var upstreamGaps = binding.Gaps.Concat(context.GlobalGaps).Distinct().Take(MaximumUpstreamGaps + 1).ToArray();
        var gaps = upstreamGaps.Take(MaximumUpstreamGaps)
            .Select(gap => new BindingDiagnosticGap(context.Bound(gap.Field)!, context.Bound(gap.Reason)!)).ToList();
        if (upstreamGaps.Length > MaximumUpstreamGaps)
            Truncate("Gaps", "Upstream diagnostic gaps exceeded the adapter limit.");
        if (upstreamGaps.Any(gap => gap.Reason.Contains("truncat", StringComparison.OrdinalIgnoreCase))) context.Truncate();

        BindingDetailValue Unavailable(string field, string reason)
        {
            var boundedReason = context.Bound(reason)!;
            gaps.Add(new BindingDiagnosticGap(field, boundedReason));
            return new(BindingDetailState.Unavailable, Reason: boundedReason);
        }

        BindingDetailValue Unknown(string field, string reason)
        {
            gaps.Add(new BindingDiagnosticGap(field, reason));
            return new(BindingDetailState.Unknown, Reason: reason);
        }

        BindingDetailValue Missing(string field, params string[] upstreamFields)
        {
            var gap = upstreamFields.Select(upstreamField => upstreamGaps.FirstOrDefault(candidate =>
                candidate.Field.Equals(upstreamField, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(candidate => candidate is not null);
            return gap is not null
                ? Unavailable(field, gap.Reason)
                : Unknown(field, "The upstream response did not provide this field or an availability reason.");
        }

        BindingDetailValue Text(string field, string upstreamField, string? value) => value is not null
            ? new(BindingDetailState.Known, context.Bound(value))
            : Missing(field, upstreamField);

        void Truncate(string field, string reason)
        {
            context.Truncate();
            gaps.Add(new BindingDiagnosticGap(field, reason));
        }

        var sourceType = binding.SourceType is { } knownSourceType
            ? new BindingDetailValue(BindingDetailState.Known, context.Bound(knownSourceType))
            : Missing("SourceType", "bindings.sourceType", "bindings.source");
        BindingDetailValue source;
        if (binding.Source is { } encoded)
        {
            source = encoded.Kind == Upstream.ValueKind.Null
                ? new(BindingDetailState.Null)
                : new(BindingDetailState.Known, context.Bound(encoded.Text ?? encoded.Summary), context.Bound(encoded.TypeName));
            if (encoded.Truncated) Truncate("Source", "The upstream source summary is truncated.");
        }
        else source = Missing("Source", "bindings.source");
        var encodedCurrentValue = binding.CurrentValue;
        BindingDetailValue currentValue = encodedCurrentValue.Kind == Upstream.ValueKind.Null
            ? new(BindingDetailState.Null, TypeName: context.Bound(encodedCurrentValue.TypeName))
            : new(BindingDetailState.Known, context.Bound(encodedCurrentValue.Text ?? encodedCurrentValue.Summary),
                context.Bound(encodedCurrentValue.TypeName));
        if (encodedCurrentValue.Truncated) Truncate("CurrentValue", "The upstream current-value summary is truncated.");
        var path = Text("Path", "bindings.path", binding.Path);
        var mode = Text("Mode", "bindings.mode", binding.Mode);
        var converterGap = upstreamGaps.FirstOrDefault(gap => gap.Field.Equals("bindings.converter", StringComparison.OrdinalIgnoreCase));
        BindingDetailValue converter = binding.ConverterType is { } converterType
            ? new BindingDetailValue(BindingDetailState.Known, context.Bound(converterType), context.Bound(converterType))
            : converterGap is not null ? Unavailable("Converter", converterGap.Reason) : new(BindingDetailState.Null);
        var parameter = Unavailable("ConverterParameter", "The upstream binding DTO does not expose converter parameters.");
        var culture = Unavailable("ConverterCulture", "The upstream binding DTO does not expose configured converter culture; CurrentCulture is not inferred.");
        var cultureOrigin = Unavailable("ConverterCultureOrigin", "The upstream binding DTO does not expose converter culture origin.");
        var fallback = Unavailable("FallbackValue", "The upstream binding DTO does not expose fallback values.");
        var targetNull = Unavailable("TargetNullValue", "The upstream binding DTO does not expose target-null values.");
        var trigger = Unavailable("UpdateSourceTrigger", "The upstream binding DTO does not expose update-source triggers.");
        var format = Unavailable("StringFormat", "The upstream binding DTO does not expose string formats.");
        var element = Unavailable("ElementName", "The upstream binding DTO does not expose element-name configuration.");
        var relative = Unavailable("RelativeSource", "The upstream binding DTO does not expose relative-source configuration.");
        var resourceKey = Unavailable("ResourceKey", "The upstream binding DTO does not expose an actual resource key or a compatible weak key handle.");

        var errors = binding.Errors.Take(MaximumErrors).Select(error =>
            new BindingErrorInfo(context.Bound(error.Category)!, context.Bound(error.Message)!)).ToArray();
        if (binding.Errors.Count > MaximumErrors) Truncate("Errors", "Upstream binding errors exceeded the adapter limit.");
        if (errors.Length > 0)
            _ = Unavailable("Errors.Exception", "The upstream binding DTO does not expose raw exceptions or inner exception chains.");
        context.Validation.AddRange(errors.Where(error => error.Category == "validation-error").Select(error => error.Message));

        var children = new List<BindingDetails>();
        foreach (var child in binding.Children.Take(MaximumChildren))
        {
            if (depth >= MaximumDepth || context.RemainingNodes == 0) break;
            children.Add(MapDetails(child, context, depth + 1));
        }
        if (children.Count < binding.Children.Count)
            Truncate("Children", "Child bindings exceeded the adapter depth, child-count, or total-node limit.");

        BindingDetailValue dataContext = context.DataContext.Type is { } dataContextType
            ? new BindingDetailValue(BindingDetailState.Known, context.Bound(dataContextType), context.Bound(dataContextType),
                Reason: "The upstream response provides the DataContext type only; no object handle or value summary is available.")
            : context.DataContext.Origin == "unknown"
                ? Unknown("DataContext", "The upstream response could not determine DataContext evidence.")
                : new(BindingDetailState.Null);
        BindingDetailValue origin = context.DataContext.Origin == "unknown"
            ? Unknown("DataContextOrigin", "The upstream response could not determine the DataContext origin.")
            : new(BindingDetailState.Known, context.Bound(context.DataContext.Origin));
        _ = Unavailable("DataContext.Value", "The upstream response does not expose a DataContext object summary or a compatible weak handle.");
        context.Limitations.AddRange(gaps.Select(gap => context.Bound(gap.Field + ": " + gap.Reason)!));
        var kind = context.Bound(binding.Kind)!;
        return new BindingDetails(kind, sourceType, source, path, mode, converter, parameter, fallback, targetNull,
            trigger, format, element, relative, children, errors, gaps, context.TruncationCount > before, dataContext, origin,
            CurrentValue: currentValue, ResourceKey: resourceKey, ConverterCulture: culture, ConverterCultureOrigin: cultureOrigin);
    }

    private sealed class MappingContext(Upstream.DataContextDiagnosticDto dataContext, IReadOnlyList<Upstream.DiagnosticGapDto> gaps)
    {
        public int RemainingNodes { get; set; } = MaximumNodes;
        private int RemainingTextUnits { get; set; } = MaximumTextUnits;
        public int TruncationCount { get; private set; }
        public Upstream.DataContextDiagnosticDto DataContext { get; } = dataContext;
        public IReadOnlyList<Upstream.DiagnosticGapDto> GlobalGaps { get; } = gaps;
        public List<string> Validation { get; } = [];
        public List<string> Limitations { get; } = [];
        public void Truncate() => TruncationCount++;
        public string? Bound(string? value)
        {
            if (value is null) return null;
            if (value is { Length: > Upstream.BindingDiagnosticRules.MaxTextLength }) Truncate();
            var bounded = Upstream.BindingDiagnosticRules.BoundText(value)!;
            if (bounded.Length <= RemainingTextUnits)
            {
                RemainingTextUnits -= bounded.Length;
                return bounded;
            }
            Truncate();
            if (RemainingTextUnits == 0) return "…";
            var prefix = RemainingTextUnits - 1;
            if (prefix > 0 && char.IsHighSurrogate(bounded[prefix - 1]) && char.IsLowSurrogate(bounded[prefix])) prefix--;
            RemainingTextUnits = 0;
            return bounded[..prefix] + "…";
        }
    }
}

