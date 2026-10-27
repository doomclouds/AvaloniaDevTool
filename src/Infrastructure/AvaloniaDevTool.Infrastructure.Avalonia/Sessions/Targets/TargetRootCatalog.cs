using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;

namespace AvaloniaDevTool.Agent;

/// <summary>Discovers configured roots and their owned presentation popups.</summary>
internal sealed class TargetRootCatalog(
    Application application,
    DiagnosticsAgentOptions options,
    Func<object, long> observe)
{
    internal event Action<IReadOnlyList<TopLevel>>? ConfiguredRootsRefreshed;
    internal event Action<IReadOnlyList<TopLevel>>? PresentationRootsRefreshed;

    internal List<TopLevel> Refresh()
    {
        var initial = options.TopLevelProvider?.Invoke() ??
                      (application.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows
                      .Cast<TopLevel>() ?? [];
        var roots = initial.Distinct().ToList();
        ConfiguredRootsRefreshed?.Invoke(roots);
        var pending = new Queue<Popup>(roots.SelectMany(root => root.OpenedPopups));
        var seen = new HashSet<Popup>();
        while (pending.TryDequeue(out var popup))
        {
            if (!seen.Add(popup) || !popup.IsOpen)
                continue;
            foreach (var nested in popup.OpenedPopups)
                pending.Enqueue(nested);
            if (popup.Child is Visual child && TopLevel.GetTopLevel(child) is { } root && !roots.Contains(root))
                roots.Add(root);
        }

        foreach (var root in roots)
            observe(root);
        PresentationRootsRefreshed?.Invoke(roots);
        return roots;
    }
}
