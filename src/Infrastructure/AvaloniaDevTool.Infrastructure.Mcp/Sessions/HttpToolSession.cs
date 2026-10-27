using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

[assembly: InternalsVisibleTo("AvaloniaDevTool.Cli.Tests")]

namespace AvaloniaDevTool.Cli.Sessions;

/// <summary>Owns tool state, desktop control and idle lifetime for one stateful HTTP MCP session.</summary>
public sealed class HttpToolSession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly DesktopControlLease _desktop;
    private readonly TimeProvider _time;
    private readonly TimeSpan _idleTimeout;
    private readonly CancellationTokenSource _expired = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDisposable? _lease;
    private TaskCompletionSource? _disposal;
    private long _lastActivity;
    private int _activeCalls;
    private int _controllingCalls;
    private bool _closing;
    private bool _filtersConfigured;

    public HttpToolSession(string? initialPipe,
        NodeGenerationAllocator generations,
        DesktopControlLease desktop,
        TimeProvider? time = null,
        TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(generations);
        ArgumentNullException.ThrowIfNull(desktop);
        _desktop = desktop;
        _time = time ?? TimeProvider.System;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(10);
        if (_idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout), "The idle timeout must be positive.");
        _lastActivity = _time.GetTimestamp();
        Session = new ToolSession(initialPipe);
        Context = new ManagedToolContext(Session, generations);
    }

    public ToolSession Session { get; }
    public ManagedToolContext Context { get; }
    public CancellationToken Expired => _expired.Token;

    /// <summary>Adds lifetime filters after the catalog's schema-validation filters.</summary>
    public void ConfigureFilters(McpServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_sync)
        {
            ThrowIfClosing();
            if (_filtersConfigured)
                throw new InvalidOperationException("HTTP session filters have already been configured.");
            _filtersConfigured = true;
        }

        options.Filters ??= new McpServerFilters();
        options.Filters.Message ??= new McpMessageFilters();
        options.Filters.Message.IncomingFilters ??= [];
        options.Filters.Message.IncomingFilters.Add(next => async (message, token) =>
        {
            Touch();
            await next(message, token).ConfigureAwait(false);
        });
        options.Filters.Request ??= new McpRequestFilters();
        options.Filters.Request.CallToolFilters ??= [];
        options.Filters.Request.CallToolFilters.Add(next => (request, token) =>
            new ValueTask<CallToolResult>(ExecuteToolAsync(request.Params!,
                cancellation => next(request, cancellation).AsTask(),
                token)));
    }

    public async Task MonitorIdleAsync(CancellationToken hostOrSessionToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(hostOrSessionToken, Expired);
        var interval = _idleTimeout < TimeSpan.FromSeconds(1) ? _idleTimeout : TimeSpan.FromSeconds(1);
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                if (TryExpire())
                    return;
                await Task.Delay(interval, _time, stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
    }

    internal async Task<CallToolResult> ExecuteToolAsync(CallToolRequestParams parameters,
        Func<CancellationToken, Task<CallToolResult>> next,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var controlsTarget = RequiresControl(parameters);
        lock (_sync)
        {
            ThrowIfClosing();
            if (controlsTarget)
                _lease ??= _desktop.Acquire(this);
            _activeCalls++;
            if (controlsTarget)
                _controllingCalls++;
            _lastActivity = _time.GetTimestamp();
        }

        var started = Stopwatch.GetTimestamp();
        var toolName = string.Concat(parameters.Name.Take(80)
            .Select(character => char.IsControl(character) ? '_' : character));
        Console.Error.WriteLine($"{DateTimeOffset.Now:O} tool={toolName} start");
        try
        {
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, Expired);
            var result = await next(stopping.Token).ConfigureAwait(false);
            Console.Error.WriteLine(
                $"{DateTimeOffset.Now:O} tool={toolName} {(result.IsError == true ? "error" : "done")} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            return result;
        }
        catch
        {
            Console.Error.WriteLine(
                $"{DateTimeOffset.Now:O} tool={toolName} error elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            throw;
        }
        finally
        {
            lock (_sync)
            {
                _activeCalls--;
                if (controlsTarget)
                    _controllingCalls--;
                _lastActivity = _time.GetTimestamp();
                // Keep a provisional lease while other target calls from this session are still queued or executing.
                if (!_closing && _controllingCalls == 0 && !Session.IsConnected)
                    ReleaseLease();
                if (_closing && _activeCalls == 0)
                    _drained.TrySetResult();
            }
        }
    }

    private static bool RequiresControl(CallToolRequestParams parameters)
    {
        if (parameters.Name == "detach")
            return false;
        if (parameters.Name != "attach-to-app")
            return true;
        return parameters.Arguments is { } arguments && arguments.TryGetValue("id", out var id) &&
               !(id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var value) && value == 0);
    }

    internal void Touch()
    {
        lock (_sync)
        {
            ThrowIfClosing();
            _lastActivity = _time.GetTimestamp();
        }
    }

    internal bool TryExpire()
    {
        lock (_sync)
        {
            if (_closing)
                return true;
            if (_activeCalls != 0 || _time.GetElapsedTime(_lastActivity, _time.GetTimestamp()) < _idleTimeout)
                return false;
            _closing = true;
            _drained.TrySetResult();
        }

        _expired.Cancel();
        return true;
    }

    private void ThrowIfClosing()
    {
        if (_closing)
            throw new McpException(
                "session_expired: This HTTP MCP session has ended. Reconnect and obtain new node IDs.");
    }

    // Callers hold _sync so release and a subsequent acquisition cannot reuse a retiring handle.
    private void ReleaseLease()
    {
        _lease?.Dispose();
        _lease = null;
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_disposal is { } existing)
                return new ValueTask(existing.Task);
            _closing = true;
            if (_activeCalls == 0)
                _drained.TrySetResult();
            completion = _disposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = CompleteDisposalAsync(completion);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource completion)
    {
        try
        {
            try
            {
                _expired.Cancel();
            }
            finally
            {
                await _drained.Task.ConfigureAwait(false);
                var cleaned = false;
                try
                {
                    await Session.DisposeAsync().ConfigureAwait(false);
                    cleaned = true;
                }
                finally
                {
                    // A failed cleanup must not grant a competitor control of a still-connected target.
                    lock (_sync)
                        if (cleaned || !Session.IsConnected)
                            ReleaseLease();
                }
            }

            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        // Keep cancellation state readable for callers already unwinding after session shutdown.
    }
}
