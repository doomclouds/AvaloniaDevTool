using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AvaloniaDevTool.Protocol;

public enum RpcTraceKind
{
    Request,
    Response,
    Notification,
    CallEnded,
    ConnectionClosed
}

public enum RpcTraceDirection
{
    Sent,
    Received,
    Local
}

/// <summary>A decoded observation; observers must not retain live payloads indefinitely.</summary>
public sealed record RpcTraceObservation(
    RpcTraceKind Kind,
    RpcTraceDirection Direction,
    long? Id,
    string? Method,
    JsonElement? Payload,
    int FrameBytes,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record RpcTraceEntry(
    long Sequence,
    string ConnectionId,
    long? RequestId,
    string Direction,
    string Kind,
    string Method,
    string State,
    DateTimeOffset StartedAt,
    double? DurationMs,
    int SentBytes,
    int ReceivedBytes,
    string? RequestJson,
    string? ResponseJson,
    string? ErrorCode,
    string? ErrorMessage,
    bool PayloadTruncated);

public sealed record RpcTraceSnapshot(
    bool IsRecording,
    bool IncludePayloads,
    long Revision,
    IReadOnlyList<RpcTraceEntry> Entries,
    int BufferedBytes,
    long EvictedCount,
    int Capacity = RpcTraceBuffer.MaximumEntries,
    int ByteBudget = RpcTraceBuffer.MaximumBufferedBytes);

/// <summary>Opt-in bounded recording with connection-scoped, bidirectional correlation.</summary>
public sealed class RpcTraceBuffer
{
    public const int MaximumEntries = 500;
    public const int MaximumBufferedBytes = 2 * 1024 * 1024;
    public const int MaximumPayloadBytes = 16 * 1024;
    private readonly object _gate = new();
    private readonly LinkedList<Stored> _entries = [];

    private readonly Dictionary<(string Connection, long Id, RpcTraceDirection Direction), LinkedListNode<Stored>>
        _requests = [];

    private bool _recording, _includePayloads;
    private long _sequence, _revision, _evicted;
    private int _bytes;

    public void Start(bool includePayloads = false)
    {
        lock (_gate)
        {
            ClearCore();
            _recording = true;
            _includePayloads = includePayloads;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _recording = false;
            foreach (var node in _requests.Values)
                if (node.Value.Entry.State == "Pending")
                    Update(node, node.Value.Entry with { State = "CaptureStopped" });
            _requests.Clear();
            Trim();
            _revision++;
        }
    }

    public void Clear()
    {
        lock (_gate)
            ClearCore();
    }

    private void ClearCore()
    {
        _entries.Clear();
        _requests.Clear();
        _bytes = 0;
        _evicted = 0;
        _revision++;
    }

    public RpcTraceSnapshot Snapshot()
    {
        lock (_gate)
            return new(_recording,
                _includePayloads,
                _revision,
                _entries.Select(item => item.Entry).ToArray(),
                _bytes,
                _evicted);
    }

    public void Observe(string connectionId, RpcTraceObservation observation)
    {
        lock (_gate)
        {
            if (!_recording)
                return;
            var now = DateTimeOffset.UtcNow;
            var stamp = Stopwatch.GetTimestamp();
            if (observation.Kind == RpcTraceKind.ConnectionClosed)
            {
                foreach (var closedKey in _requests.Keys.Where(item => item.Connection == connectionId).ToArray())
                {
                    var pending = _requests[closedKey];
                    if (pending.Value.Entry.State == "Pending")
                        Update(pending,
                            pending.Value.Entry with
                            {
                                State = "Disconnected", DurationMs = Elapsed(pending, stamp),
                                ErrorCode = observation.ErrorCode ?? "connection_closed"
                            });
                    _requests.Remove(closedKey);
                }

                Trim();
                _revision++;
                return;
            }

            var keyDirection = observation.Kind is RpcTraceKind.Response
                ? observation.Direction == RpcTraceDirection.Received
                    ? RpcTraceDirection.Sent
                    : RpcTraceDirection.Received
                : observation.Kind == RpcTraceKind.CallEnded
                    ? RpcTraceDirection.Sent
                    : observation.Direction;
            var key = (connectionId, observation.Id ?? 0, keyDirection);
            if (observation.Kind == RpcTraceKind.CallEnded && !_requests.ContainsKey(key))
            {
                var completed = _entries.Last;
                while (completed is not null && !(completed.Value.Entry.ConnectionId == connectionId &&
                                                  completed.Value.Entry.RequestId == observation.Id &&
                                                  completed.Value.Entry.Direction == nameof(RpcTraceDirection.Sent) &&
                                                  completed.Value.Entry.Kind == nameof(RpcTraceKind.Request)))
                    completed = completed.Previous;
                if (completed is not null)
                {
                    if (completed.Value.Entry.State == "Succeeded")
                    {
                        Update(completed,
                            completed.Value.Entry with
                            {
                                State = "ClientFailed", DurationMs = Elapsed(completed, stamp),
                                ErrorCode = Limit(observation.ErrorCode, 128)
                            });
                        Trim();
                        _revision++;
                    }

                    return;
                }
            }

            if (observation.Kind is RpcTraceKind.Response or RpcTraceKind.CallEnded && observation.Id is not null &&
                _requests.TryGetValue(key, out var existing))
            {
                var previous = existing.Value.Entry;
                // The response observation precedes InvokeAsync's local exception path.
                if (observation.Kind == RpcTraceKind.CallEnded && previous.State != "Pending")
                    return;
                var payload = _includePayloads && observation.Kind == RpcTraceKind.Response
                    ? Project(observation.Payload)
                    : default;
                var state = observation.Kind == RpcTraceKind.CallEnded ? "CancelledOrFailed"
                    : previous.State == "CancelledOrFailed" ? "LateResponse"
                    : observation.ErrorCode is null ? "Succeeded" : "Failed";
                Update(existing,
                    previous with
                    {
                        State = state, DurationMs = Elapsed(existing, stamp),
                        SentBytes = previous.SentBytes +
                                    (observation.Direction == RpcTraceDirection.Sent ? observation.FrameBytes : 0),
                        ReceivedBytes = previous.ReceivedBytes +
                                        (observation.Direction == RpcTraceDirection.Received
                                            ? observation.FrameBytes
                                            : 0),
                        ResponseJson = payload.Text ?? previous.ResponseJson,
                        ErrorCode = Limit(observation.ErrorCode, 128) ?? previous.ErrorCode,
                        ErrorMessage = Limit(observation.ErrorMessage, 512) ?? previous.ErrorMessage,
                        PayloadTruncated = previous.PayloadTruncated || payload.Truncated
                    });
                if (observation.Kind == RpcTraceKind.Response)
                    _requests.Remove(key);
            }
            else
            {
                var payload = _includePayloads ? Project(observation.Payload) : default;
                var state = observation.Kind == RpcTraceKind.Request ? "Pending" :
                    observation.Kind == RpcTraceKind.Notification ? "Observed" : "Uncorrelated";
                var entry = new RpcTraceEntry(++_sequence,
                    connectionId,
                    observation.Id,
                    observation.Direction.ToString(),
                    observation.Kind.ToString(),
                    Limit(observation.Method, 256) ?? "(unmatched response)",
                    state,
                    now,
                    null,
                    observation.Direction == RpcTraceDirection.Sent ? observation.FrameBytes : 0,
                    observation.Direction == RpcTraceDirection.Received ? observation.FrameBytes : 0,
                    observation.Kind == RpcTraceKind.Response ? null : payload.Text,
                    observation.Kind == RpcTraceKind.Response ? payload.Text : null,
                    Limit(observation.ErrorCode, 128),
                    Limit(observation.ErrorMessage, 512),
                    payload.Truncated);
                var node = _entries.AddLast(new Stored(entry, stamp, Size(entry)));
                _bytes += node.Value.Bytes;
                if (observation.Kind == RpcTraceKind.Request && observation.Id is not null)
                    _requests[key] = node;
            }

            Trim();
            _revision++;
        }
    }

    private static double Elapsed(LinkedListNode<Stored> node, long now) =>
        Stopwatch.GetElapsedTime(node.Value.Timestamp, now).TotalMilliseconds;

    private void Update(LinkedListNode<Stored> node, RpcTraceEntry entry)
    {
        var size = Size(entry);
        _bytes += size - node.Value.Bytes;
        node.Value = new(entry, node.Value.Timestamp, size);
    }

    private void Trim()
    {
        while (_entries.First is { } first && (_entries.Count > MaximumEntries || _bytes > MaximumBufferedBytes))
        {
            _bytes -= first.Value.Bytes;
            _entries.RemoveFirst();
            _evicted++;
            foreach (var key in _requests.Where(pair => ReferenceEquals(pair.Value, first)).Select(pair => pair.Key)
                         .ToArray())
                _requests.Remove(key);
        }
    }

    private static int Size(RpcTraceEntry entry) => 256 + Encoding.UTF8.GetByteCount(entry.ConnectionId + entry.Method +
        entry.State + entry.ErrorCode + entry.ErrorMessage + entry.RequestJson + entry.ResponseJson);

    private static string? Limit(string? value, int length) =>
        value is null || value.Length <= length ? value : value[..length] + "…";

    private sealed record Stored(RpcTraceEntry Entry, long Timestamp, int Bytes);

    private static (string? Text, bool Truncated) Project(JsonElement? payload)
    {
        if (payload is null)
            return (null, false);
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        var remaining = 128;
        var truncated = false;

        void Write(JsonElement element, int depth)
        {
            if (--remaining < 0 || depth >= 12 ||
                writer.BytesCommitted + writer.BytesPending > MaximumPayloadBytes - 4096)
            {
                writer.WriteStringValue("[truncated]");
                truncated = true;
                return;
            }

            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        if (remaining <= 0 || writer.BytesCommitted + writer.BytesPending > MaximumPayloadBytes - 4096)
                        {
                            writer.WriteString("$trace", "[truncated]");
                            truncated = true;
                            break;
                        }

                        writer.WritePropertyName(Limit(property.Name, 96)!);
                        if (property.Name.Length > 96)
                            truncated = true;
                        if (Sensitive(property.Name))
                        {
                            remaining--;
                            writer.WriteStringValue("[redacted]");
                        }
                        else
                            Write(property.Value, depth + 1);
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        if (remaining <= 0 || writer.BytesCommitted + writer.BytesPending > MaximumPayloadBytes - 4096)
                        {
                            writer.WriteStringValue("[truncated]");
                            truncated = true;
                            break;
                        }

                        Write(item, depth + 1);
                    }

                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    var text = element.GetString();
                    if (text?.Length > 512)
                        truncated = true;
                    writer.WriteStringValue(Limit(text, 512));
                    break;
                case JsonValueKind.Number:
                    if (element.TryGetInt64(out var integer))
                        writer.WriteNumberValue(integer);
                    else if (element.TryGetDouble(out var number) && double.IsFinite(number))
                        writer.WriteNumberValue(number);
                    else
                    {
                        writer.WriteStringValue("[number omitted]");
                        truncated = true;
                    }

                    break;
                case JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;
                case JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;
                default:
                    writer.WriteNullValue();
                    break;
            }
        }

        Write(payload.Value, 0);
        writer.Flush();
        return (Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length), truncated);
    }

    private static bool Sensitive(string name) => new[]
            { "token", "password", "authorization", "secret", "credential", "apikey", "privatekey" }
        .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
}
