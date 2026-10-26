using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private readonly Dictionary<long, WeakReference<AvaloniaObject>> _observed = [];
    private readonly HashSet<long> _pendingProperties = [];
    private readonly DispatcherTimer _timer;
    private string _rootsSnapshot = "";

    private long Observe(object value)
    {
        var id = _objects.GetId(value);
        if (value is AvaloniaObject obj && !_observed.ContainsKey(id))
        {
            _observed.Add(id, new WeakReference<AvaloniaObject>(obj));
            obj.PropertyChanged += OnPropertyChanged;
            if (obj is TemplatedControl owner)
                owner.TemplateApplied += OnMergedTemplateApplied;
        }

        return id;
    }

    private string Fingerprint(IEnumerable<object> children) =>
        string.Join(",", children.Select(_objects.GetId));

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!_disposed && sender is AvaloniaObject obj)
            _pendingProperties.Add(_objects.GetId(obj));
    }

    private void OnMergedTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (_disposed || sender is not TemplatedControl owner)
            return;
        _mergedTree.Invalidate(owner);
        if (_treeObservations.Keys.Any(key => key.Id == _objects.GetId(owner)))
            Notify(Methods.TreeInvalidated, new TreeInvalidatedNotification(_objects.GetId(owner)));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
            return;
        var roots = RefreshRoots();
        var rootsSnapshot = Fingerprint(roots);
        RefreshPropertyContexts();
        if (rootsSnapshot != _rootsSnapshot)
        {
            _rootsSnapshot = rootsSnapshot;
            Notify(Methods.TreeInvalidated, new TreeInvalidatedNotification());
        }

        foreach (var id in _pendingProperties.ToArray())
            Notify(Methods.PropertiesChanged, new PropertiesChangedNotification(id));
        _pendingProperties.Clear();
        foreach (var id in _observed.Where(x => !x.Value.TryGetTarget(out _)).Select(x => x.Key).ToArray())
            _observed.Remove(id);
        _objects.Prune();
    }
}
