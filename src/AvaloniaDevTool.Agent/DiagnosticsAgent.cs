using System.IO.Pipes;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

public sealed partial class DiagnosticsAgent : IDisposable, IAsyncDisposable
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
                await using var pipe = new NamedPipeServerStream(_options.PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                _pipe = pipe;
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                await ServeConnectionAsync(pipe, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (RpcException ex) when (ex.Code == "session_busy") { }
            catch (IOException ex)
            {
                if (!_stop.IsCancellationRequested)
                {
                    LastError = ex.Message;
                    try { await Task.Delay(150, _stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
                }
            }
            catch (Exception ex) when (!_stop.IsCancellationRequested)
            {
                LastError = ex.Message;
                try { await Task.Delay(150, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            }
            finally
            {
                _pipe = null;
            }
        }
    }

    private static async Task<object?> HandleOnUiThreadAsync(AgentSession session, string method,
        JsonElement parameters, CancellationToken cancellation)
    {
        Task<object?>? pending = null;
        await Dispatcher.UIThread.InvokeAsync(new Action(() => pending = session.HandleAsync(method, parameters, cancellation)),
            DispatcherPriority.Normal, cancellation);
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
}

