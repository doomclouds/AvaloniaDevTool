using Avalonia;
using Avalonia.LogicalTree;
using System.Reflection;
using Avalonia.Styling;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal static class ElementSelectorBuilder
{
    private const int MaximumValidationNodes = 20000;

    public static ElementSelectorResult Build(StyledElement target,
        IEnumerable<Visual> roots,
        int maximumValidationNodes = MaximumValidationNodes)
    {
        if (maximumValidationNodes is < 1 or > MaximumValidationNodes)
            throw new ArgumentOutOfRangeException(nameof(maximumValidationNodes));
        var candidates = new List<StyledElement>();
        var pending = new Queue<object>(roots);
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        while (seen.Count < maximumValidationNodes && pending.TryDequeue(out var value))
        {
            if (!seen.Add(value))
                continue;
            if (value is StyledElement element)
                candidates.Add(element);
            if (value is Visual visual)
                foreach (var child in visual.GetVisualChildren())
                    pending.Enqueue(child);
            if (value is ILogical logical)
                foreach (var child in logical.LogicalChildren)
                    pending.Enqueue(child);
        }

        var truncated = pending.Count != 0;
        if (!candidates.Contains(target))
            return Unsupported("The element is detached from the current session roots.");
        var types = candidates.Select(element => element.GetType()).Distinct().GroupBy(type => type.Name)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        try
        {
            var simple = target.TemplatedParent is StyledElement
                ? Path(target, new HashSet<StyledElement>(ReferenceEqualityComparer.Instance))
                : Segment(target);
            var result = Validate(simple);
            if (result.MatchCount <= 1)
                return result;
            try
            {
                var path = Path(target, new HashSet<StyledElement>(ReferenceEqualityComparer.Instance));
                return Validate(path);
            }
            catch (NotSupportedException)
            {
                return result with
                {
                    Detail =
                    "The selector is ambiguous; its ancestor path requires an unsupported namespace mapping or identifier."
                };
            }
        }
        catch (NotSupportedException exception)
        {
            return Unsupported(exception.Message);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException
                                              or TargetInvocationException or TypeLoadException
                                              or MissingMethodException)
        {
            return Unsupported("Avalonia rejected the generated selector: " + exception.Message);
        }

        ElementSelectorResult Validate(string text)
        {
            var parserType =
                Type.GetType("Avalonia.Markup.Parsers.SelectorParser, Avalonia.Markup", throwOnError: true)!;
            Func<string, string, Type> resolve = (prefix, name) =>
                string.IsNullOrEmpty(prefix) && types.TryGetValue(name, out var type)
                    ? type
                    : throw new NotSupportedException("The selector type requires a namespace mapping.");
            var parser = Activator.CreateInstance(parserType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [resolve],
                null)!;
            var selector = (Selector)parserType.GetMethod("Parse",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(parser, [text])!;
            var match = typeof(Selector).GetMethod("Match",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            var isMatch = match.ReturnType.GetProperty("IsMatch")!;
            if (!(bool)isMatch.GetValue(match.Invoke(selector, [target, null, false]))!)
                return Unsupported("The generated selector does not match the selected element's actual style key.");
            var matches = candidates.Count(element =>
                (bool)isMatch.GetValue(match.Invoke(selector, [element, null, false]))!);
            return new(true,
                text,
                !truncated && matches == 1,
                matches,
                truncated,
                truncated ? $"Validation reached the {maximumValidationNodes}-node limit; uniqueness is unverified."
                : matches == 1 ? "Unique within the current session roots."
                : "The selector matches multiple current elements.");
        }
    }

    private static string Path(StyledElement target, HashSet<StyledElement> seen)
    {
        if (seen.Count >= 512 || !seen.Add(target))
            throw new NotSupportedException("The selector ancestor path is too deep or cyclic.");
        var segment = Segment(target);
        if (target.TemplatedParent is StyledElement templateOwner)
            return Path(templateOwner, seen) + " /template/ " + segment;
        if (target is ILogical { LogicalParent: StyledElement parent })
            return Path(parent, seen) + " > " + segment;
        return segment;
    }

    private static string Segment(StyledElement target)
    {
        var type = target.GetType();
        if (type.Assembly != typeof(Avalonia.Controls.Control).Assembly || !Identifier(type.Name))
            throw new NotSupportedException("Custom control types require an explicit XAML namespace mapping.");
        if (target.Name is { Length: > 0 } name && !Identifier(name))
            throw new NotSupportedException(
                "This name contains characters that Avalonia selector identifiers cannot represent safely.");
        var classes = target.Classes.Where(name => !name.StartsWith(':')).Order(StringComparer.Ordinal).ToArray();
        if (classes.Any(name => !Identifier(name)))
            throw new NotSupportedException(
                "A class contains characters that Avalonia selector identifiers cannot represent safely.");
        return type.Name + (string.IsNullOrEmpty(target.Name) ? "" : "#" + target.Name) +
               string.Concat(classes.Select(name => "." + name));
    }

    private static bool Identifier(string name) => name.Length > 0 &&
                                                   (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
                                                   name.All(character =>
                                                       char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static ElementSelectorResult Unsupported(string detail) => new(false, null, false, 0, false, detail);
}
