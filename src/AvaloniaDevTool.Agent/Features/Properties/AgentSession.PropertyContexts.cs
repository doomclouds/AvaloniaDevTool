using Avalonia;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private readonly Dictionary<long, long?> _propertyContextParents = [];

    private long? PropertyContextParentId(AvaloniaObject target) =>
        ContextualProperties.Parent(target) is { } parent ? _objects.GetId(parent) : null;

    private void RefreshPropertyContexts()
    {
        foreach (var entry in _propertyContextParents.ToArray())
        {
            if (!_observed.TryGetValue(entry.Key, out var weak) || !weak.TryGetTarget(out var target))
            {
                _propertyContextParents.Remove(entry.Key);
                continue;
            }

            var parent = PropertyContextParentId(target);
            if (parent == entry.Value)
                continue;
            _propertyContextParents[entry.Key] = parent;
            _pendingProperties.Add(entry.Key);
        }
    }
}
