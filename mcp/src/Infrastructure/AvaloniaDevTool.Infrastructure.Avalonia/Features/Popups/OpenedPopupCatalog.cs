using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;

namespace AvaloniaDevTool.Agent;

/// <summary>Uses the framework catalog when available; observes popup opening on older Avalonia versions.</summary>
internal sealed class OpenedPopupCatalog : IObserver<AvaloniaPropertyChangedEventArgs<bool>>, IDisposable
{
    private static readonly PropertyInfo? TopLevelPopups = typeof(TopLevel).GetProperty("OpenedPopups");
    private static readonly PropertyInfo? NestedPopups = typeof(Popup).GetProperty("OpenedPopups");
    private readonly List<WeakReference<Popup>> _opened = [];
    private readonly IDisposable _subscription;

    public OpenedPopupCatalog() => _subscription = Popup.IsOpenProperty.Changed.Subscribe(this);

    public IReadOnlyList<Popup> GetOpened(Control owner)
    {
        var property = owner is TopLevel ? TopLevelPopups : NestedPopups;
        if (property?.GetValue(owner) is IEnumerable<Popup> known)
            return known.ToArray();

        // The fallback also discovers logical popups already open when the Agent was attached.
        var root = owner as TopLevel ?? (owner is Popup { Child: Visual child } ? TopLevel.GetTopLevel(child) : null);
        if (root is null)
            return [];
        _opened.RemoveAll(reference => !reference.TryGetTarget(out var popup) || !popup.IsOpen);
        return _opened.Select(reference => reference.TryGetTarget(out var popup) ? popup : null)
            .OfType<Popup>().Concat(owner.GetLogicalDescendants().OfType<Popup>())
            .Where(popup => popup.IsOpen && popup.PlacementTarget is { } target &&
                            ReferenceEquals(TopLevel.GetTopLevel(target), root))
            .Distinct().ToArray();
    }

    public void OnNext(AvaloniaPropertyChangedEventArgs<bool> change)
    {
        if (change.Sender is Popup popup && change.NewValue.GetValueOrDefault() &&
            !_opened.Any(reference => reference.TryGetTarget(out var value) && ReferenceEquals(value, popup)))
            _opened.Add(new(popup));
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }
    public void Dispose()
    {
        _subscription.Dispose();
        _opened.Clear();
    }
}
