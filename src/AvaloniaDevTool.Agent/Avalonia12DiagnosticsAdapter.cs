using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Styling;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Contains the version-specific reflection boundary for Avalonia 12 PrivateApi diagnostics.</summary>
internal static class Avalonia12DiagnosticsAdapter
{
    public static StyleSnapshot Inspect(AvaloniaObject target, Func<object, long>? registerWeakObject = null)
    {
        if (typeof(AvaloniaObject).Assembly.GetName().Version?.Major != 12)
            throw new RpcException("adapter_unavailable", "This diagnostics adapter requires Avalonia 12.");
        var frames = new List<StyleFrameInfo>();
        var bindings = new List<BindingDiagnosticInfo>();
        var limitations = new List<string>();
        var reader = new BindingDetailsReader(registerWeakObject);
        var truncated = false;
        var remainingSetters = 1024;
        // These runtime members are intentionally absent from Avalonia's reference assemblies.
        object? store = null;
        try { store = target.GetValueStoreDiagnostic(); }
        catch (Exception error) { limitations.Add("Style frame diagnostics unavailable: " + error.GetType().Name); }
        var applied = Read(store, "AppliedFrames") as IEnumerable;
        if (applied is null) limitations.Add("The Avalonia 12 AppliedFrames diagnostics shape is unavailable.");
        var rawFrames = applied?.Cast<object>().Take(257).ToArray() ?? [];
        truncated |= rawFrames.Length > 256;
        foreach (var frame in rawFrames.Take(256))
        {
            var values = new List<StyleSetterInfo>();
            if (Read(frame, "Values") is IEnumerable entries)
            {
                var rawValues = entries.Cast<object>().Take(Math.Min(513, remainingSetters + 1)).ToArray();
                var count = Math.Min(512, remainingSetters);
                truncated |= rawValues.Length > count;
                foreach (var entry in rawValues.Take(count))
                {
                    if (Read(entry, "Property") is not AvaloniaProperty property) continue;
                    var entryValue = BindingDetailsReader.Probe(entry, "Value");
                    var value = entryValue.Value;
                    bool? matches = null;
                    try { if (entryValue.Available) matches = Equals(target.GetValue(property), value); }
                    catch (Exception error) { limitations.Add("Setter comparison unavailable: " + property.Name + ": " + error.GetType().Name); }
                    values.Add(new StyleSetterInfo(property.Name, property.OwnerType.FullName ?? property.OwnerType.Name,
                        SafeText(value, reader), matches));
                    --remainingSetters;
                }
            }
            var sourceObject = Read(frame, "Source");
            var source = sourceObject switch
            {
                Style style => SafeText(style.Selector, reader) ?? style.GetType().Name,
                ControlTheme theme => theme.TargetType?.FullName ?? "ControlTheme",
                null => SafeText(Read(frame, "Type"), reader) ?? "Unknown",
                _ => SafeText(sourceObject, reader) ?? sourceObject.GetType().Name
            };
            frames.Add(new StyleFrameInfo(reader.Text(source)!, SafeText(Read(frame, "Type"), reader) ?? "Unknown",
                SafeText(Read(frame, "Priority"), reader) ?? "Unknown", Read(frame, "IsActive") is true, values));
        }
        var properties = AvaloniaPropertyRegistry.Instance.GetRegistered(target.GetType())
            .Concat(AvaloniaPropertyRegistry.Instance.GetRegisteredAttached(target.GetType())).Distinct();
        foreach (var property in properties)
        {
            BindingExpressionBase? expression;
            try { expression = BindingOperations.GetBindingExpressionBase(target, property); }
            catch (Exception expressionError) { limitations.Add("Binding expression getter unavailable: " + property.Name + ": " + expressionError.GetType().Name); continue; }
            if (expression is null) continue;
            if (!reader.HasCapacity) { truncated = true; break; }
            var description = SafeText(Read(expression, "Description"), reader);
            var error = SafeText(Read(expression, "ErrorType"), reader);
            if (description is null || error is null)
                limitations.Add("Partial expression diagnostics for " + property.Name + ": runtime members are unavailable.");
            string? diagnostic = null;
            try { diagnostic = reader.Text(target.GetDiagnostic(property).Diagnostic); }
            catch (Exception diagnosticError) { limitations.Add("Property diagnostic unavailable: " + property.Name + ": " + diagnosticError.GetType().Name); }
            var details = reader.Read(expression, target);
            bindings.Add(new BindingDiagnosticInfo(property.Name, description ?? SafeText(expression, reader) ?? expression.GetType().Name,
                expression.GetType().FullName ?? expression.GetType().Name, error ?? "Unknown",
                Read(expression, "IsRunning") as bool?, SafeText(Read(expression, "Priority"), reader) ?? "Unknown",
                diagnostic, details));
        }
        var errors = new List<string>();
        if (target is Control control)
            try
            {
                var validation = DataValidationErrors.GetErrors(control)?.Take(33).ToArray() ?? [];
                truncated |= validation.Length > 32;
                errors.AddRange(validation.Take(32).Select(error => SafeText(error, reader) ?? "(null)"));
            }
            catch (Exception error) { limitations.Add("Validation diagnostics unavailable: " + error.GetType().Name); }
        return new StyleSnapshot(frames, bindings, errors, "Avalonia 12.1.4 PrivateApi adapter", truncated || reader.Truncated,
            limitations.Select(note => reader.Text(note)!).Take(64).ToArray());
    }

    private static object? Read(object? target, string property) => BindingDetailsReader.Probe(target, property).Value;

    private static string? SafeText(object? value, BindingDetailsReader reader)
    {
        try { return reader.Text(PropertyValues.Format(value)); }
        catch (Exception error) { return reader.Text("(display unavailable: " + error.GetType().Name + ")"); }
    }
}

