using System.Text.Json;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Decodes each registered request only when that method is invoked.</summary>
internal sealed class RpcMethodRegistry
{
    private readonly Dictionary<string, Func<JsonElement, object>> _sync = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> _async =
        new(StringComparer.Ordinal);

    internal void Map<TRequest>(string method, Func<TRequest, object> handler) =>
        _sync.Add(method, parameters => handler(ProtocolJson.Read<TRequest>(parameters)));

    internal void Map(string method, Func<object> handler) => _sync.Add(method, _ => handler());

    internal void MapAsync<TRequest>(string method, Func<TRequest, CancellationToken, Task<object?>> handler) =>
        _async.Add(method,
            (parameters, cancellation) => handler(ProtocolJson.Read<TRequest>(parameters), cancellation));

    internal object Invoke(string? method, JsonElement parameters) =>
        method is not null && _sync.TryGetValue(method, out var handler)
            ? handler(parameters)
            : throw new RpcException("method_not_found", $"Unknown method: {method}");

    internal Task<object?> InvokeAsync(string? method, JsonElement parameters, CancellationToken cancellation) =>
        method is not null && _async.TryGetValue(method, out var handler)
            ? handler(parameters, cancellation)
            : Task.FromResult<object?>(Invoke(method, parameters));
}
