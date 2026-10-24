using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

public sealed partial class DiagnosticsAgent
{
    /// <summary>Serves one native RPC connection, taking ownership of the duplex stream.</summary>
    public async Task ServeConnectionAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            await ServeConnectionAsync(_ => Task.FromResult(stream), cancellationToken).ConfigureAwait(false);
        }
        finally { await stream.DisposeAsync().ConfigureAwait(false); }
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
                throw new ArgumentException("The diagnostic stream must be readable and writable.", nameof(streamFactory));
            lock (_connectionSync)
                _activeStream = stream;
            stopping.Token.ThrowIfCancellationRequested();
            using var registration = stopping.Token.Register(static state => ((Stream)state!).Dispose(), stream);
            session = await Dispatcher.UIThread.InvokeAsync(
                () => new AgentSession(_application, _options), DispatcherPriority.Normal, stopping.Token);
            _session = session;
            await using var peer = new RpcPeer(stream, (method, parameters, cancellation) =>
                HandleOnUiThreadAsync(session, method, parameters, cancellation));
            await Dispatcher.UIThread.InvokeAsync(() => session.Connect(peer), DispatcherPriority.Normal, stopping.Token);
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
}
