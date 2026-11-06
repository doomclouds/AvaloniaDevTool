using System.IO.Pipes;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

public sealed class DiagnosticsAgent : IDisposable, IAsyncDisposable
{
    private readonly Application _application;

    private readonly DiagnosticsAgentOptions _options;

    private readonly CancellationTokenSource _stop = new();

    private readonly Task _serverTask;

    private NamedPipeServerStream? _pipe;

    private volatile AgentSession? _session;

    private readonly object _connectionSync = new();

    private TaskCompletionSource? _connectionEnded;

    private Stream? _activeStream;

    private int _disposed;

    private DiagnosticsAgent(Application application, DiagnosticsAgentOptions options)
    {
        Dispatcher.UIThread.VerifyAccess();
        _application = application;
        _options = options;
        if (string.IsNullOrWhiteSpace(options.PipeName) || options.PipeName.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new ArgumentException("PipeName must be a simple local pipe name.", nameof(options));
        _serverTask = Task.Run(ListenAsync);
    }

    public string PipeName => _options.PipeName;

    public bool IsConnected => _session is not null;

    public string? LastError { get; private set; }

    public static DiagnosticsAgent Attach(Application application, DiagnosticsAgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        return new DiagnosticsAgent(application, options ?? new DiagnosticsAgentOptions());
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_options.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _pipe = pipe;
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                await ServeConnectionAsync(pipe, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
            {
            }
            catch (RpcException ex) when (ex.Code == "session_busy")
            {
            }
            catch (IOException ex)
            {
                if (!_stop.IsCancellationRequested)
                {
                    LastError = ex.Message;
                    try
                    {
                        await Task.Delay(150, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                    }
                }
            }
            catch (Exception ex) when (!_stop.IsCancellationRequested)
            {
                LastError = ex.Message;
                try
                {
                    await Task.Delay(150, _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }
            }
            finally
            {
                _pipe = null;
            }
        }
    }

    private static async Task<object?> HandleOnUiThreadAsync(AgentSession session,
        string method,
        JsonElement parameters,
        CancellationToken cancellation)
    {
        Task<object?>? pending = null;
        await Dispatcher.UIThread.InvokeAsync(
            new Action(() => pending = session.HandleAsync(method, parameters, cancellation)),
            DispatcherPriority.Normal,
            cancellation);
        return await pending!.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _pipe?.Dispose();
        lock (_connectionSync)
            _activeStream?.Dispose();
        if (Dispatcher.UIThread.CheckAccess())
            _session?.Dispose();
        else if (_session is { } session)
            Dispatcher.UIThread.Post(session.Dispose);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _serverTask.ConfigureAwait(false);
        Task? active;
        lock (_connectionSync)
            active = _connectionEnded?.Task;
        if (active is not null)
            await active.ConfigureAwait(false);
        // Keep cancellation state available for callers that entered before disposal.
    }

    /// <summary>Serves one native RPC connection, taking ownership of the duplex stream.</summary>
    public async Task ServeConnectionAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            await ServeConnectionAsync(_ => Task.FromResult(stream), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Reserves the shared session before creating or upgrading a transport.</summary>
    /// <remarks>The factory is not invoked when another pipe or remote session is active.</remarks>
    public async Task ServeConnectionAsync(Func<CancellationToken, Task<Stream>> streamFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(streamFactory);
        TaskCompletionSource ended;
        lock (_connectionSync)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_connectionEnded is not null)
                throw new RpcException("session_busy", "The diagnostic application already has an active session.");
            ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _connectionEnded = ended;
        }

        AgentSession? session = null;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        try
        {
            await using var stream = await streamFactory(stopping.Token).ConfigureAwait(false);
            if (!stream.CanRead || !stream.CanWrite)
                throw new ArgumentException("The diagnostic stream must be readable and writable.",
                    nameof(streamFactory));
            lock (_connectionSync)
                _activeStream = stream;
            stopping.Token.ThrowIfCancellationRequested();
            using var registration = stopping.Token.Register(static state => ((Stream)state!).Dispose(), stream);
            session = await Dispatcher.UIThread.InvokeAsync(
                () => new AgentSession(_application, _options),
                DispatcherPriority.Normal,
                stopping.Token);
            _session = session;
            await using var peer = new RpcPeer(stream,
                (method, parameters, cancellation) =>
                    HandleOnUiThreadAsync(session, method, parameters, cancellation));
            await Dispatcher.UIThread.InvokeAsync(() => session.Connect(peer),
                DispatcherPriority.Normal,
                stopping.Token);
            peer.Start();
            await peer.Completion.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (session is not null)
                    await Dispatcher.UIThread.InvokeAsync(session.Dispose);
            }
            finally
            {
                _session = null;
                lock (_connectionSync)
                {
                    _activeStream = null;
                    _connectionEnded = null;
                    ended.TrySetResult();
                }
            }
        }
    }

    public void NotifyContentChanged(long generation)
    {
        Dispatcher.UIThread.VerifyAccess();
        _session?.NotifyContentChanged(generation);
    }
}
