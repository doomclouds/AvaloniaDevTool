using ModelContextProtocol;

namespace AvaloniaDevTool.Cli.Sessions;

/// <summary>Allows one MCP session to control this host's desktop at a time.</summary>
public sealed class DesktopControlLease
{
    private readonly object _sync = new();
    private Lease? _current;

    public IDisposable Acquire(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        lock (_sync)
        {
            if (_current is { IsReleased: false } existing)
            {
                if (ReferenceEquals(existing.Owner, owner))
                    return existing;
                throw new McpException(
                    "controller_busy: Another MCP session controls this desktop. Detach or close that session first.");
            }

            return _current = new Lease(this, owner);
        }
    }

    private void Release(Lease lease)
    {
        lock (_sync)
            if (ReferenceEquals(_current, lease))
                _current = null;
    }

    private sealed class Lease(DesktopControlLease registry, object owner) : IDisposable
    {
        private DesktopControlLease? _registry = registry;
        internal object Owner { get; } = owner;
        internal bool IsReleased => Volatile.Read(ref _registry) is null;

        public void Dispose() => Interlocked.Exchange(ref _registry, null)?.Release(this);
    }
}
