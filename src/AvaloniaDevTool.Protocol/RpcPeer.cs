using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace AvaloniaDevTool.Protocol;

/// <summary>A small duplex RPC peer using bounded, length-prefixed UTF-8 JSON frames.</summary>
public sealed class RpcPeer : IAsyncDisposable
{
    public const int MaximumFrameBytes = 4 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly Func<string, JsonElement, CancellationToken, Task<object?>>? _requestHandler;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<RpcMessage> _requests = Channel.CreateBounded<RpcMessage>(new BoundedChannelOptions(64)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private Task _completion = Task.CompletedTask;
    private Task _requestWorker = Task.CompletedTask;
    private int _queuedWrites;
    private long _nextId;
    private int _started;
    private int _disposed;

    public RpcPeer(Stream stream, Func<string, JsonElement, CancellationToken, Task<object?>>? requestHandler = null)
    {
        _stream = stream;
        _requestHandler = requestHandler;
    }

    public event Action<string, JsonElement>? NotificationReceived;
    public event Action<RpcTraceObservation>? TraceObserved;
    public Task Completion => _completion;
    public Exception? CloseException { get; private set; }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The RPC peer has already started.");
        _requestWorker = ProcessRequestsAsync();
        _completion = ReadLoopAsync();
    }

    public async Task<T> InvokeAsync<T>(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        cancellationToken.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("The request identifier is already in use.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var registration = timeout.Token.Register(() =>
        {
            if (_pending.TryRemove(id, out var request))
                request.TrySetCanceled(timeout.Token);
        });
        try
        {
            await WriteAsync(new RpcMessage(id, method, ToElement(parameters), null, null), timeout.Token).ConfigureAwait(false);
            var result = await completion.Task.ConfigureAwait(false);
            return result.Deserialize(ProtocolJson.TypeInfo<T>())
                ?? throw new RpcException("invalid_response", "The server returned an empty response.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_stopping.IsCancellationRequested)
        {
            PublishTrace(new(RpcTraceKind.CallEnded, RpcTraceDirection.Local, id, method, null, 0, "request_timeout"));
            throw new RpcException("request_timeout", "The diagnostic request timed out.");
        }
        catch (Exception exception)
        {
            PublishTrace(new(RpcTraceKind.CallEnded, RpcTraceDirection.Local, id, method, null, 0,
                exception is RpcException rpc ? rpc.Code : exception is OperationCanceledException ? "request_cancelled"
                    : exception is JsonException ? "invalid_response" : "request_failed"));
            throw;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task SendNotificationAsync(string method, object? payload, CancellationToken cancellationToken = default)
    {
        EnsureRunning();
        return WriteAsync(new RpcMessage(null, method, ToElement(payload), null, null), cancellationToken);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            var header = new byte[sizeof(int)];
            while (!_stopping.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _stopping.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (length <= 0 || length > MaximumFrameBytes)
                    throw new RpcException("invalid_frame", "The incoming frame length is outside the protocol limit.");
                var bytes = new byte[length];
                await _stream.ReadExactlyAsync(bytes, _stopping.Token).ConfigureAwait(false);
                var message = JsonSerializer.Deserialize(bytes, ProtocolJson.TypeInfo<RpcMessage>())
                    ?? throw new RpcException("invalid_frame", "The incoming frame is empty.");
                ObserveFrame(message, RpcTraceDirection.Received, length);
                if (message.Method is { Length: > 0 } method)
                {
                    if (message.Id is { } requestId)
                    {
                        if (!_requests.Writer.TryWrite(message))
                            throw new RpcException("request_queue_full", "The diagnostic request queue is full.");
                    }
                    else
                        PublishNotification(method, message.Parameters ?? ToElement(null));
                }
                else if (message.Id is { } responseId && _pending.TryRemove(responseId, out var completion))
                {
                    if (message.Error is { } error)
                        completion.TrySetException(new RpcException(error.Code, error.Message));
                    else if (message.Result is { } result)
                        completion.TrySetResult(result);
                    else
                        completion.TrySetException(new RpcException("invalid_response", "The response has no result."));
                }
                else if (message.Id is null)
                {
                    throw new RpcException("invalid_frame", "A frame must contain a method or response identifier.");
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (Exception exception)
        {
            CloseException = exception;
        }
        finally
        {
            PublishTrace(new(RpcTraceKind.ConnectionClosed, RpcTraceDirection.Local, null, null, null, 0,
                CloseException is RpcException rpc ? rpc.Code : "connection_closed"));
            await _stopping.CancelAsync().ConfigureAwait(false);
            _requests.Writer.TryComplete();
            foreach (var entry in _pending)
                if (_pending.TryRemove(entry.Key, out var request))
                    request.TrySetException(new RpcException("connection_closed", "The diagnostic connection was closed."));
            await _stream.DisposeAsync().ConfigureAwait(false);
            await _requestWorker.ConfigureAwait(false);
        }
    }

    private async Task ProcessRequestsAsync()
    {
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
                await HandleRequestAsync(request.Id!.Value, request.Method!, request.Parameters ?? ToElement(null)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_stopping.IsCancellationRequested) CloseException = exception;
            await _stopping.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleRequestAsync(long id, string method, JsonElement parameters)
    {
        RpcMessage response;
        try
        {
            var result = _requestHandler is null
                ? throw new RpcException("unknown_method", "The peer does not handle requests.")
                : await _requestHandler(method, parameters, _stopping.Token).ConfigureAwait(false);
            response = new RpcMessage(id, null, null, ToElement(result), null);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { return; }
        catch (Exception exception)
        {
            var error = exception is RpcException rpc
                ? new RpcError(rpc.Code, rpc.Message)
                : exception is JsonException
                    ? new RpcError("invalid_request", "The request payload could not be parsed.")
                    : new RpcError("request_failed", exception.Message);
            response = new RpcMessage(id, null, null, null, error);
        }
        try
        {
            await WriteAsync(response, _stopping.Token).ConfigureAwait(false);
        }
        catch (RpcException exception) when (exception.Code == "frame_too_large")
        {
            await WriteAsync(new RpcMessage(id, null, null, null,
                new RpcError(exception.Code, "The response exceeds the 4 MiB frame limit; narrow the inspection query.")),
                _stopping.Token).ConfigureAwait(false);
        }
    }

    private void PublishNotification(string method, JsonElement parameters)
    {
        if (NotificationReceived is not { } handlers)
            return;
        foreach (Action<string, JsonElement> handler in handlers.GetInvocationList())
        {
            // A UI observer must not terminate the transport.
            try { handler(method, parameters); }
            catch { }
        }
    }

    private async Task WriteAsync(RpcMessage message, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _queuedWrites) > 128)
        {
            Interlocked.Decrement(ref _queuedWrites);
            throw new RpcException("send_queue_full", "The diagnostic send queue is full.");
        }
        try { await WriteCoreAsync(message, cancellationToken).ConfigureAwait(false); }
        finally { Interlocked.Decrement(ref _queuedWrites); }
    }

    private async Task WriteCoreAsync(RpcMessage message, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var frameStarted = false;
        try
        {
            EnsureRunning();
            // Serialize only the active writer so queued notifications do not each allocate a 4 MiB buffer.
            var bytes = ProtocolJson.SerializeToUtf8Bytes(message);
            if (bytes.Length > MaximumFrameBytes)
                throw new RpcException("frame_too_large", "The outgoing frame exceeds the protocol limit.");
            var header = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
            // Once a frame starts, only connection shutdown may interrupt it.
            ObserveFrame(message, RpcTraceDirection.Sent, bytes.Length);
            frameStarted = true;
            await _stream.WriteAsync(header, _stopping.Token).ConfigureAwait(false);
            await _stream.WriteAsync(bytes, _stopping.Token).ConfigureAwait(false);
            await _stream.FlushAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch when (frameStarted)
        {
            await _stopping.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void EnsureRunning()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _started) == 0)
            throw new InvalidOperationException("Start the RPC peer before sending messages.");
        if (_stopping.IsCancellationRequested)
            throw new RpcException("connection_closed", "The diagnostic connection was closed.");
    }

    private void ObserveFrame(RpcMessage message, RpcTraceDirection direction, int bytes)
    {
        if (TraceObserved is null) return;
        var kind = message.Method is { Length: > 0 }
            ? message.Id is null ? RpcTraceKind.Notification : RpcTraceKind.Request : RpcTraceKind.Response;
        PublishTrace(new(kind, direction, message.Id, message.Method, message.Parameters ?? message.Result,
            bytes, message.Error?.Code, message.Error?.Message));
    }

    private void PublishTrace(RpcTraceObservation observation)
    {
        if (TraceObserved is not { } observers) return;
        foreach (Action<RpcTraceObservation> observer in observers.GetInvocationList())
        {
            try { observer(observation); }
            catch { /* Diagnostic observers cannot change transport behavior. */ }
        }
    }

    private static JsonElement ToElement(object? value) => ProtocolJson.ToElement(value);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        await _completion.ConfigureAwait(false);
        // Keep synchronization primitives alive for already-started callers unwinding after cancellation.
    }

    internal sealed record RpcMessage(long? Id, string? Method, JsonElement? Parameters, JsonElement? Result, RpcError? Error);
    internal sealed record RpcError(string Code, string Message);
}

