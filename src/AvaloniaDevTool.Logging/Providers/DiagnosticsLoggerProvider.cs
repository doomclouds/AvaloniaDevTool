using AvaloniaDevTool.Agent;
using Microsoft.Extensions.Logging;

namespace AvaloniaDevTool.Logging;

/// <summary>
/// An opt-in Microsoft logging provider and agent log source. Register the same instance with both
/// ILoggerFactory.AddProvider and DiagnosticsAgentOptions.LogSources. No global logging configuration is changed.
/// </summary>
[ProviderAlias("AvaloniaDevTool")]
public sealed class DiagnosticsLoggerProvider : ILoggerProvider, ISupportExternalScope, IDiagnosticsLogSource
{
    private readonly DiagnosticsLogSource _source = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();
    private volatile bool _disposed;
    [ThreadStatic] private static bool _publishing;

    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new DiagnosticsLogger(this, categoryName);
    }

    public IDisposable Subscribe(Action<DiagnosticsLogEvent> observer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _source.Subscribe(observer);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        _scopes = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));

    public void Dispose() => _disposed = true;

    private bool IsEnabled(LogLevel level) =>
        !_disposed && _source.HasSubscribers && level is >= LogLevel.Trace and < LogLevel.None;

    private void Publish<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception)
    {
        if (!IsEnabled(level) || _publishing)
            return;
        _publishing = true;
        try
        {
            var parameters = ReadState(state, out var template);
            var scopes = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
            _scopes.ForEachScope((scope, target) =>
                {
                    if (target.Count >= 8)
                        return;
                    var values = ReadState(scope, out _);
                    target.Add(values ?? [new KeyValuePair<string, object?>("Scope", scope)]);
                },
                scopes);
            var source = parameters?.FirstOrDefault(pair => pair.Key == "Source").Value;
            _source.Publish(new DiagnosticsLogEvent(MapLevel(level),
                category,
                template,
                parameters,
                exception,
                source,
                eventId.Id,
                eventId.Name,
                scopes,
                "Microsoft.Extensions.Logging"));
        }
        catch
        {
            /* User-defined state and scope implementations must not disrupt the application. */
        }
        finally
        {
            _publishing = false;
        }
    }

    private static IReadOnlyList<KeyValuePair<string, object?>>? ReadState<TState>(TState state, out string? template)
    {
        template = state as string;
        if (state is string)
            return [];
        var result = new List<KeyValuePair<string, object?>>();
        if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            for (var index = 0; index < Math.Min(values.Count, 34); index++)
            {
                var pair = values[index];
                if (pair.Key == "{OriginalFormat}")
                    template = pair.Value as string;
                else if (result.Count < 33)
                    result.Add(pair);
            }

            if (values.Count > 34 && values[^1] is { Key: "{OriginalFormat}", Value: string original })
                template = original;
            return result;
        }

        if (state is not IEnumerable<KeyValuePair<string, object?>> pairs)
            return null;
        using var enumerator = pairs.GetEnumerator();
        for (var index = 0; index < 34 && enumerator.MoveNext(); index++)
        {
            var pair = enumerator.Current;
            if (pair.Key == "{OriginalFormat}")
                template = pair.Value as string;
            else if (result.Count < 33)
                result.Add(pair);
        }

        return result;
    }

    private static string MapLevel(LogLevel level) => level switch
    {
        LogLevel.Trace => "Verbose", LogLevel.Critical => "Fatal", _ => level.ToString()
    };

    private sealed class DiagnosticsLogger(DiagnosticsLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => owner._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => owner.Publish(category, logLevel, eventId, state, exception);
    }
}
