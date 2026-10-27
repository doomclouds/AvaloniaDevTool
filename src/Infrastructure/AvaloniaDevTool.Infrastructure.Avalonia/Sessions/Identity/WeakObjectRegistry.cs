using System.Runtime.CompilerServices;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed class WeakObjectRegistry
{
    private readonly ConditionalWeakTable<object, IdHolder> _ids = new();
    private readonly Dictionary<long, WeakReference<object>> _objects = [];
    private long _nextId;

    public long GetId(object value)
    {
        if (_ids.TryGetValue(value, out var holder))
            return holder.Id;
        var id = ++_nextId;
        _ids.Add(value, new IdHolder(id));
        _objects.Add(id, new WeakReference<object>(value));
        return id;
    }

    public object Get(long id) =>
        _objects.TryGetValue(id, out var reference) && reference.TryGetTarget(out var target)
            ? target
            : throw new RpcException("object_expired", "The object no longer exists. Refresh the tree.");

    public bool TryGet(long id, out object? value)
    {
        value = null;
        return _objects.TryGetValue(id, out var reference) && reference.TryGetTarget(out value);
    }

    public void Prune()
    {
        foreach (var id in _objects.Where(x => !x.Value.TryGetTarget(out _)).Select(x => x.Key).ToArray())
            _objects.Remove(id);
    }

    private sealed record IdHolder(long Id);
}
