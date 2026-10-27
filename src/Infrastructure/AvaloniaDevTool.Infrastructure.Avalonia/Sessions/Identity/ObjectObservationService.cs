using Avalonia;
using Avalonia.Controls.Primitives;

namespace AvaloniaDevTool.Agent;

/// <summary>Owns weak framework subscriptions; consumers decide what a change invalidates.</summary>
internal sealed class ObjectObservationService(WeakObjectRegistry objects) : IDisposable
{
    private readonly Dictionary<long, WeakReference<AvaloniaObject>> _observed = [];
    private bool _disposed;
    internal event Action<long>? PropertyChanged;
    internal event Action<TemplatedControl>? TemplateApplied;

    internal long Observe(object value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = objects.GetId(value);
        if (value is AvaloniaObject target && !_observed.ContainsKey(id))
        {
            _observed.Add(id, new(target));
            target.PropertyChanged += OnPropertyChanged;
            if (target is TemplatedControl owner)
                owner.TemplateApplied += OnTemplateApplied;
        }

        return id;
    }

    internal bool TryGet(long id, out AvaloniaObject target)
    {
        target = null!;
        return _observed.TryGetValue(id, out var reference) && reference.TryGetTarget(out target!);
    }

    internal void Prune()
    {
        foreach (var id in _observed.Where(entry => !entry.Value.TryGetTarget(out _)).Select(entry => entry.Key)
                     .ToArray())
            _observed.Remove(id);
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (!_disposed && sender is AvaloniaObject target)
            PropertyChanged?.Invoke(objects.GetId(target));
    }

    private void OnTemplateApplied(object? sender, TemplateAppliedEventArgs args)
    {
        if (!_disposed && sender is TemplatedControl owner)
            TemplateApplied?.Invoke(owner);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var reference in _observed.Values)
        {
            if (!reference.TryGetTarget(out var target))
                continue;
            target.PropertyChanged -= OnPropertyChanged;
            if (target is TemplatedControl owner)
                owner.TemplateApplied -= OnTemplateApplied;
        }

        _observed.Clear();
        PropertyChanged = null;
        TemplateApplied = null;
    }
}
