// The diagnostic vocabulary and classification approach are adapted from XamlMcp,
// copyright (c) 2026 Rahul TR, Apache-2.0, commit 1718f946cb275a71e021de58f4caa552e04feaa8.
// Changes: per-field states, isolated Avalonia 12 probes, weak IDs, exception trees,
// composite expressions, and a shared response budget. See ThirdParty/THIRD-PARTY-NOTICES.md.
using System.Collections;
using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Diagnostics;
using Avalonia.Markup.Xaml.MarkupExtensions;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>All non-reference-assembly binding reflection lives inside this compatibility boundary.</summary>
internal sealed class BindingDetailsReader(Func<object, long>? registerWeakObject = null, bool allowPrivateApi = true)
{
    internal const int MaxExpressions = 128;
    internal const int MaxTextLength = 256;
    private const int MaxChildren = 32;
    private const int MaxDepth = 6;
    private const int MaxErrorDepth = 4;
    private const int MaxErrorCount = 16;
    private int _remainingExpressions = MaxExpressions;
    private int _remainingText = 32768;
    private int _truncations;
    public bool Truncated { get; private set; }
    public bool HasCapacity => _remainingExpressions > 0;

    public BindingDetails Read(object expression, AvaloniaObject target) => Read(expression, target, 0);

    private BindingDetails Read(object expression, AvaloniaObject target, int depth)
    {
        var initialTruncations = _truncations;
        --_remainingExpressions;
        var gaps = new List<BindingDiagnosticGap>();
        var type = expression.GetType();
        var kind = type.FullName switch
        {
            "Avalonia.Data.Core.BindingExpression" => "binding",
            "Avalonia.Data.Core.MultiBindingExpression" => "multi-binding",
            "Avalonia.Data.TemplateBindingExpression" => "template-binding",
            "Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExpression" => "dynamic-resource",
            _ => "unknown"
        };
        var knownShape = kind != "unknown" && type.Assembly == (kind == "dynamic-resource"
                ? typeof(DynamicResourceExtension).Assembly : typeof(AvaloniaObject).Assembly) &&
            type.Assembly.GetName().Version is { Major: 12, Minor: 1 };
        var privateShape = allowPrivateApi && knownShape;
        var composite = kind == "multi-binding";
        var template = kind == "template-binding";
        var resource = kind == "dynamic-resource";
        var nodes = privateShape && kind == "binding" ? Probe(expression, "_nodes", true) : ProbeResult.Missing;
        var rawNodes = BoundedItems(nodes.Value, MaxChildren, out var nodesTruncated);
        var leaf = Probe(expression, "LeafNode");
        var leafSource = Probe(leaf.Value, "Source");
        var leafValue = Probe(leaf.Value, "Value");
        var source = ProbeResult.Missing;
        if (resource) source = privateShape ? Probe(expression, "_host", true) : ProbeResult.Missing;
        else if (template) source = new(true, (target as StyledElement)?.TemplatedParent);
        else if (!composite)
        {
            if (rawNodes.Count > 0 && IsSourceNode(rawNodes[0].GetType()))
                source = Probe(rawNodes[0], "Value");
            else if (privateShape && Probe(expression, "_source", true).Value is WeakReference<object?> weak)
                source = new(true, weak.TryGetTarget(out var value) ? value : null);
            else if (leafSource.Available)
                source = leafSource;
        }
        if (ReferenceEquals(source.Value, AvaloniaProperty.UnsetValue)) source = new(true, null);
        var sourceDetail = composite ? Unavailable("Source", "A composite expression has no single source.", gaps) :
            Value(source, "Source", gaps, navigation: true);
        var sourceType = source.Available ? source.Value is null ? Null() : Known(TypeName(source.Value.GetType())) :
            Unavailable("SourceType", "The runtime did not expose a binding source; the leaf value type is not a source type.", gaps);
        var description = Probe(expression, "Description");
        var path = resource ? Unavailable("Path", "A resource expression has a ResourceKey rather than a CLR path.", gaps) :
            composite ? Unavailable("Path", "A composite expression has no single path.", gaps) : template ?
            Value(privateShape ? PropertyNameProbe(expression, "_property") : ProbeResult.Missing, "Path", gaps) :
            Value(description, "Path", gaps);
        var modeProbe = privateShape && !composite ? Probe(expression, "_mode", true) : ProbeResult.Missing;
        var mode = modeProbe.Value is BindingMode modeValue && modeValue != BindingMode.Default ? Known(modeValue.ToString()) :
            Unavailable("Mode", composite ? "The composite expression does not retain an effective mode." :
                "The effective mode is not exposed by the available runtime shape.", gaps);
        BindingDetailValue Member(string name) => Value(composite && !privateShape ? ProbeResult.Missing : Probe(expression, name, composite), name, gaps);
        var converterProbe = template && privateShape ? Probe(expression, "_converter", true) :
            composite && !privateShape ? ProbeResult.Missing : Probe(expression, "Converter", composite);
        var converter = converterProbe.Available ? converterProbe.Value is null ? Null() : Known(TypeName(converterProbe.Value.GetType())) :
            Unavailable("Converter", "The runtime did not expose a converter for this expression.", gaps);
        var converterParameter = template ? Value(privateShape ? Probe(expression, "_converterParameter", true) : ProbeResult.Missing,
            "ConverterParameter", gaps) : Member("ConverterParameter");
        var (culture, cultureOrigin) = ReadConverterCulture(expression, kind, knownShape, privateShape, gaps);
        var fallback = template ? Unavailable("FallbackValue", "TemplateBinding has no fallback value.", gaps) : Member("FallbackValue");
        var targetNull = template ? Unavailable("TargetNullValue", "TemplateBinding has no target-null value.", gaps) : Member("TargetNullValue");
        var trigger = knownShape && (template || composite || resource) ? NotApplicable("This expression has no configurable update-source trigger.") : Member("UpdateSourceTrigger");
        var format = knownShape && (template || resource) ? NotApplicable("This expression has no StringFormat option.") : composite ?
            !converterProbe.Available ? Unavailable("StringFormat", "The composite converter shape is unavailable.", gaps) :
            converterProbe.Value is StringFormatMultiValueConverter formatConverter ?
                Value(new(true, formatConverter.Format), "StringFormat", gaps) :
                new BindingDetailValue(BindingDetailState.Null, Reason: Text("No framework string-format wrapper is retained; the original declaration may have been omitted or ignored.")) :
            Member("StringFormat");
        var currentValue = Value(ProbeCachedValue(expression), "CurrentValue", gaps);
        var resourceKey = ReadResourceKey(resource && privateShape ? Probe(expression, "_resourceKey", true) : ProbeResult.Missing, gaps);
        var elementName = Unavailable("ElementName", "The original ElementName declaration is not retained by the expression diagnostics contract.", gaps);
        var relativeSource = template ? Known("TemplatedParent") : Unavailable("RelativeSource", "The original RelativeSource declaration is not retained by the expression diagnostics contract.", gaps);
        var dataContext = target is StyledElement element ? Value(new(true, element.DataContext), "DataContext", gaps, navigation: true) :
            Unavailable("DataContext", "The target is not a StyledElement.", gaps);
        BindingDetailValue origin;
        try
        {
            origin = target is StyledElement ? Known(target.GetDiagnostic(StyledElement.DataContextProperty).Priority.ToString()) :
                Unavailable("DataContextOrigin", "The target is not a StyledElement.", gaps);
        }
        catch (Exception error) { origin = Unavailable("DataContextOrigin", "Diagnostic getter failed: " + TypeName(error.GetType()), gaps); }

        var errorType = Probe(expression, "ErrorType").Value?.ToString();
        var errors = new List<BindingErrorInfo>();
        var rawError = privateShape ? Probe(expression, "_error", true) : ProbeResult.Missing;
        var rawException = Probe(rawError.Value, "Exception").Value as Exception;
        if (errorType is not null and not "None")
        {
            var category = errorType == "DataValidationError" ? "validation-error" :
                source.Available && source.Value is null && !composite ? "missing-source" :
                leafSource.Value is not null && ReferenceEquals(leafValue.Value, AvaloniaProperty.UnsetValue) ? "path-error" :
                converterProbe.Value is not null && rawException is InvalidCastException ? "converter-error" : "binding-error";
            var errorBudget = MaxErrorCount;
            errors.Add(rawException is not null ? DescribeError(rawException, category, 0, ref errorBudget) :
                new BindingErrorInfo(category, Text("Avalonia reported " + errorType + ".") ?? ""));
            if (rawException is null) gaps.Add(new("Errors.Exception", Text("The runtime did not expose the raw binding exception.")!));
        }
        else if (errorType is null)
            gaps.Add(new("Errors", Text("The runtime error state is unavailable; no healthy state is inferred.")!));
        var children = new List<BindingDetails>();
        var localTruncated = nodesTruncated;
        if (composite)
        {
            var expressions = privateShape ? Probe(expression, "Expressions", true) : ProbeResult.Missing;
            if (!expressions.Available) gaps.Add(new("Children", Text("The composite child expression shape is unavailable.")!));
            else
            {
                var rawChildren = BoundedItems(expressions.Value, MaxChildren, out var childTruncated);
                localTruncated |= childTruncated;
                foreach (var child in rawChildren)
                {
                    if (depth >= MaxDepth || _remainingExpressions <= 0) { localTruncated = true; break; }
                    children.Add(Read(child, target, depth + 1));
                }
            }
        }
        localTruncated |= errors.Any(error => error.Truncated) || children.Any(child => child.Truncated) || _truncations > initialTruncations;
        if (localTruncated) { Truncated = true; gaps.Add(new("Children", "Expression or exception details reached the response bound.")); }
        return new BindingDetails(kind, sourceType, sourceDetail, path, mode, converter, converterParameter, fallback,
            targetNull, trigger, format, elementName, relativeSource, children, errors, gaps, localTruncated, dataContext, origin, currentValue, resourceKey,
            culture, cultureOrigin);
    }

    private (BindingDetailValue Culture, BindingDetailValue Origin) ReadConverterCulture(object expression, string kind,
        bool knownShape, bool privateShape, List<BindingDiagnosticGap> gaps)
    {
        if (knownShape && kind == "dynamic-resource")
            return (NotApplicable("A resource expression has no ConverterCulture option."),
                NotApplicable("A resource expression has no converter culture origin."));

        if (!privateShape)
            return (Unavailable("ConverterCulture", "The configured culture requires a verified Avalonia 12.1 expression storage shape.", gaps),
                Unavailable("ConverterCultureOrigin", "No verified culture storage is available; CurrentCulture is not sampled or inferred.", gaps));

        // BindingExpression's public getter substitutes CurrentCulture, losing configuration evidence.
        var storage = kind == "binding" ? Probe(expression, "_uncommon", true) : new ProbeResult(true, expression);
        var probe = storage.Available && storage.Value is null ? new ProbeResult(true, null) :
            storage.Available ? Probe(storage.Value, "_converterCulture", true) : storage;
        if (!probe.Available)
            return (Unavailable("ConverterCulture", probe.Error ?? "The verified expression did not expose configured culture storage.", gaps),
                Unavailable("ConverterCultureOrigin", "The configured culture storage could not be read.", gaps));

        if (probe.Value is null)
            return (new(BindingDetailState.Null, Reason: Text("No configured culture is retained; Avalonia uses CurrentCulture when conversion or formatting occurs.")),
                new(BindingDetailState.Known, Text("CurrentCulture fallback"), Reason: Text("Verified null culture storage; the runtime culture is not sampled by diagnostics.")));

        // A CultureInfo subclass can override Name; diagnostic reads must not invoke custom getters.
        if (probe.Value.GetType() != typeof(CultureInfo))
            return (Unavailable("ConverterCulture", "A custom culture object is retained; its Name getter and ToString are not invoked.", gaps),
                new(BindingDetailState.Known, Text("Configured"), Reason: Text("Non-null culture storage was observed without evaluating the custom object.")));

        var culture = (CultureInfo)probe.Value;
        return (new(BindingDetailState.Known, Text(culture.Name.Length == 0 ? "(InvariantCulture)" : culture.Name), Text(typeof(CultureInfo).FullName)),
            new(BindingDetailState.Known, Text("Configured"), Reason: Text(kind == "binding" ?
                "Observed BindingExpression._uncommon._converterCulture; the original binding declaration is not retained." :
                "Observed expression._converterCulture; the original binding declaration is not retained.")));
    }

    private BindingDetailValue ReadResourceKey(ProbeResult probe, List<BindingDiagnosticGap> gaps)
    {
        if (!probe.Available)
            return Unavailable("ResourceKey", probe.Error ?? "No verified resource key is available for this expression shape.", gaps);
        if (probe.Value is null) return Null();
        if (probe.Value is string key)
        {
            var text = Text(key);
            if (text != key)
                return Unavailable("ResourceKey", "The string resource key exceeded the diagnostic text budget; its preview cannot identify a resource.", gaps);
            return new(BindingDetailState.Known, text, typeof(string).FullName);
        }
        long? id = null;
        if (registerWeakObject is not null)
            try { id = registerWeakObject(probe.Value); }
            catch (Exception error) { gaps.Add(new("ResourceKey.ObjectId", Text("Weak key registration failed: " + TypeName(error.GetType()))!)); }
        // A resource key can be an arbitrary object; a display string is never its identity.
        return new(BindingDetailState.Known, Text(TypeName(probe.Value.GetType())), Text(TypeName(probe.Value.GetType())), id,
            id is null ? "The non-string key has no Native weak object handle; its display text cannot identify a resource." : null);
    }

    internal BindingErrorInfo DescribeError(Exception error, string category)
    {
        var budget = MaxErrorCount;
        return DescribeError(error, category, 0, ref budget);
    }

    private BindingErrorInfo DescribeError(Exception error, string category, int depth, ref int budget)
    {
        --budget;
        var children = new List<BindingErrorInfo>();
        var inner = error is AggregateException aggregate ? aggregate.InnerExceptions :
            error.InnerException is { } exception ? new[] { exception }.AsEnumerable() : [];
        var truncated = false;
        foreach (var child in inner)
        {
            if (depth >= MaxErrorDepth || budget <= 0) { truncated = true; break; }
            children.Add(DescribeError(child, category, depth + 1, ref budget));
        }
        string message;
        // AggregateException.Message recursively concatenates every child, bypassing our tree budget.
        try { message = error is AggregateException ? "One or more errors occurred (see bounded inner exceptions)." : error.Message; }
        catch (Exception getterError) { message = "Exception.Message getter failed: " + TypeName(getterError.GetType()); }
        truncated |= message.Length > MaxTextLength || children.Any(child => child.Truncated);
        if (truncated) Truncated = true;
        return new(category, Text(message) ?? "", Text(TypeName(error.GetType())), children, truncated);
    }

    private BindingDetailValue Value(ProbeResult result, string field, List<BindingDiagnosticGap> gaps, bool navigation = false)
    {
        if (!result.Available) return Unavailable(field, result.Error ?? "The runtime member is unavailable for this expression.", gaps);
        if (result.Value is null) return Null();
        if (ReferenceEquals(result.Value, AvaloniaProperty.UnsetValue)) return Known("(UnsetValue)");
        long? id = null;
        if (navigation && registerWeakObject is not null && !result.Value.GetType().IsValueType && result.Value is not string)
            try { id = registerWeakObject(result.Value); }
            catch (Exception error) { gaps.Add(new(field + ".ObjectId", Text("Weak object registration failed: " + TypeName(error.GetType()))!)); }
        try
        {
            var display = result.Value is IFormattable formattable ? formattable.ToString(null, CultureInfo.InvariantCulture) : result.Value.ToString();
            return new(BindingDetailState.Known, Text(display), Text(TypeName(result.Value.GetType())), id);
        }
        catch (Exception error)
        {
            var reason = Text("Display getter failed: " + TypeName(error.GetType()));
            gaps.Add(new(field, reason!));
            return new(BindingDetailState.Unavailable, TypeName(result.Value.GetType()), Text(TypeName(result.Value.GetType())), id, reason);
        }
    }

    private BindingDetailValue Known(string value) => new(BindingDetailState.Known, Text(value));
    private static BindingDetailValue Null() => new(BindingDetailState.Null);
    private BindingDetailValue NotApplicable(string reason) => new(BindingDetailState.NotApplicable, Reason: Text(reason));
    private BindingDetailValue Unavailable(string field, string reason, List<BindingDiagnosticGap> gaps)
    {
        reason = Text(reason)!;
        gaps.Add(new(field, reason));
        return new(BindingDetailState.Unavailable, Reason: reason);
    }

    internal string? Text(string? text)
    {
        if (text is null) return null;
        var limit = Math.Min(MaxTextLength, _remainingText);
        if (text.Length <= limit) { _remainingText -= text.Length; return text; }
        Truncated = true;
        ++_truncations;
        if (limit <= 1) return "…";
        var length = limit - 1;
        if (char.IsHighSurrogate(text[length - 1])) --length;
        _remainingText -= length + 1;
        return text[..length] + "…";
    }

    internal static ProbeResult Probe(object? target, string member, bool nonPublic = false)
    {
        if (target is null) return ProbeResult.Missing;
        try
        {
            for (var type = target.GetType(); type is not null; type = type.BaseType)
            {
                var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                if (nonPublic) flags |= BindingFlags.NonPublic;
                if (type.GetProperty(member, flags) is { } property && property.GetIndexParameters().Length == 0)
                    return new(true, property.GetValue(target));
                if (nonPublic && type.GetField(member, flags) is { } field) return new(true, field.GetValue(target));
            }
            return ProbeResult.Missing;
        }
        catch (Exception error)
        {
            if (error is TargetInvocationException { InnerException: { } inner }) error = inner;
            return new(false, null, "Runtime getter failed: " + TypeName(error.GetType()));
        }
    }

    private static ProbeResult PropertyNameProbe(object expression, string member)
    {
        var result = Probe(expression, member, true);
        return result.Value is AvaloniaProperty property ? new(true, property.Name) : result;
    }

    private static ProbeResult ProbeCachedValue(object expression)
    {
        try
        {
            // This method returns the framework's cached value and never calls UpdateTarget/UpdateSource.
            var method = expression.GetType().GetMethod("GetValue", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
            return method is null ? ProbeResult.Missing : new(true, method.Invoke(expression, null));
        }
        catch (Exception error)
        {
            if (error is TargetInvocationException { InnerException: { } inner }) error = inner;
            return new(false, null, "Cached value getter failed: " + TypeName(error.GetType()));
        }
    }

    private static bool IsSourceNode(Type? type)
    {
        for (; type is not null; type = type.BaseType)
            if (type.FullName == "Avalonia.Data.Core.ExpressionNodes.SourceNode") return true;
        return false;
    }

    private static List<object> BoundedItems(object? value, int limit, out bool truncated)
    {
        truncated = false;
        var result = new List<object>();
        if (value is not IEnumerable enumerable) return result;
        try
        {
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ == limit) { truncated = true; break; }
                if (item is not null) result.Add(item);
            }
        }
        catch { truncated = true; }
        return result;
    }

    private static string TypeName(Type type) => type.FullName ?? type.Name;
    internal readonly record struct ProbeResult(bool Available, object? Value, string? Error = null)
    {
        public static ProbeResult Missing => new(false, null);
    }
}
