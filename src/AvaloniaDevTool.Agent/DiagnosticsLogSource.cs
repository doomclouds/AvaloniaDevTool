namespace AvaloniaDevTool.Agent;

/// <summary>An opt-in application log source. Subscriptions last only while inspector log capture is running.</summary>
public interface IDiagnosticsLogSource
{
    IDisposable Subscribe(Action<DiagnosticsLogEvent> observer);
}

/// <summary>
/// A synchronous log emission. Null parameters mean unavailable; an empty list means known to be empty.
/// Values, exceptions and source objects are snapshotted immediately and are not retained by the agent.
/// </summary>
public sealed record DiagnosticsLogEvent(string Level, string Category, string? MessageTemplate,
    IReadOnlyList<KeyValuePair<string, object?>>? Parameters = null, Exception? Exception = null,
    object? Source = null, int? EventId = null, string? EventName = null,
    IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>>? Scopes = null, string Origin = "Custom");

/// <summary>Publishes custom application logs to an explicitly configured diagnostics agent.</summary>
public sealed class DiagnosticsLogSource : IDiagnosticsLogSource
{
    private readonly object _gate = new();
    private readonly List<Action<DiagnosticsLogEvent>> _observers = [];

    public bool HasSubscribers { get { lock (_gate) return _observers.Count > 0; } }

    public IDisposable Subscribe(Action<DiagnosticsLogEvent> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate) _observers.Add(observer);
        return new Subscription(this, observer);
    }

    public void Publish(DiagnosticsLogEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Action<DiagnosticsLogEvent>[] observers;
        lock (_gate) observers = _observers.ToArray();
        foreach (var observer in observers)
        {
            try { observer(entry); }
            catch { /* Diagnostics must never disrupt application logging. */ }
        }
    }

    private sealed class Subscription(DiagnosticsLogSource owner, Action<DiagnosticsLogEvent> observer) : IDisposable
    {
        private DiagnosticsLogSource? _owner = owner;
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) lock (current._gate) current._observers.Remove(observer);
        }
    }
}
