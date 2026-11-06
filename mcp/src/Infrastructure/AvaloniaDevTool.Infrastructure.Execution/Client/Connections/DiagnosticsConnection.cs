using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

internal sealed class DiagnosticsConnection : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _notificationSync = new();
    private RpcPeer? _peer;
    private NotificationSource? _notificationSource;
    private long _revision;
    private int _disposed;
    public bool IsConnected => _peer is not null && Session is not null;
    public SessionInfo? Session { get; private set; }
    public RpcTraceBuffer ProtocolTrace { get; } = new();

    public event Action? ConnectionChanged;

    public Task ConnectAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        return ConnectStreamAsync(async token =>
            {
                var stream = new NamedPipeClientStream(".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await stream.ConnectAsync(token).ConfigureAwait(false);
                    return stream;
                }
                catch
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            },
            cancellationToken);
    }

    private async Task ConnectStreamAsync(Func<CancellationToken, Task<Stream>> streamFactory,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await DisconnectCoreAsync().ConfigureAwait(false);
            Stream? stream = null;
            RpcPeer? peer = null;
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                stream = await streamFactory(connectTimeout.Token).ConfigureAwait(false);
                peer = new RpcPeer(stream);
                var traceConnection = Guid.NewGuid().ToString("N");
                peer.TraceObserved += observation => ProtocolTrace.Observe(traceConnection, observation);
                lock (_notificationSync)
                {
                    var source = new NotificationSource(this, peer, Revision);
                    _notificationSource = source;
                    peer.NotificationReceived += source.Handler;
                }

                peer.Start();
                var info = await peer.InvokeAsync<SessionInfo>(Methods.SessionInfo, null, connectTimeout.Token)
                    .ConfigureAwait(false);
                if (info.ProtocolVersion != DiagnosticsProtocol.Version)
                    throw new RpcException("unsupported_version",
                        $"Protocol {info.ProtocolVersion} is not supported by this client.");
                lock (_notificationSync)
                {
                    Session = info;
                    SessionChanged?.Invoke(info);
                    _peer = peer;
                }

                RaiseConnectionChanged();
                _ = ObserveDisconnectionAsync(peer);
            }
            catch
            {
                RetireNotifications();
                if (peer is not null)
                    await peer.DisposeAsync().ConfigureAwait(false);
                else if (stream is not null)
                    await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task DisconnectCoreAsync()
    {
        var peer = _peer;
        RetireNotifications();
        if (peer is not null)
        {
            await peer.DisposeAsync().ConfigureAwait(false);
            RaiseConnectionChanged();
        }
    }

    private async Task ObserveDisconnectionAsync(RpcPeer peer)
    {
        await peer.Completion.ConfigureAwait(false);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_peer, peer))
                await DisconnectCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void RaiseConnectionChanged()
    {
        if (ConnectionChanged is not { } handlers)
            return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try
            {
                handler();
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await DisconnectAsync().ConfigureAwait(false);
    }

    /// <summary>Connects to a native RPC WebSocket endpoint using an Authorization header.</summary>
    /// <remarks>Plaintext is restricted to literal loopback addresses; normal TLS validation is retained.</remarks>
    public Task ConnectRemoteAsync(Uri endpoint, string bearerToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != "ws" && endpoint.Scheme != "wss") ||
            !string.IsNullOrEmpty(endpoint.Fragment) || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query))
            throw new ArgumentException("A ws/wss endpoint without user information, query or fragment is required.",
                nameof(endpoint));
        if (endpoint.Scheme == "ws" &&
            !(IPAddress.TryParse(endpoint.DnsSafeHost, out var address) && IPAddress.IsLoopback(address)))
            throw new ArgumentException(
                "Plaintext WebSockets require a literal loopback address; use wss for remote hosts.",
                nameof(endpoint));
        if (string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Length > 4096 || bearerToken.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("A base64url bearer token is required.", nameof(bearerToken));
        return ConnectStreamAsync(async token =>
            {
                var socket = new ClientWebSocket();
                if (endpoint.Scheme == "ws")
                    socket.Options.Proxy = null;
                socket.Options.SetRequestHeader("Authorization", "Bearer " + bearerToken);
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
                try
                {
                    await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
                    return new RpcWebSocketStream(socket);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
            cancellationToken);
    }

    internal RpcPeer Peer =>
        _peer ?? throw new RpcException("not_connected", "Connect to a diagnostic application first.");

    internal event Action<string, JsonElement>? NotificationReceived;
    internal event Action<SessionInfo?>? SessionChanged;
    internal long Revision => Interlocked.Read(ref _revision);

    /// <summary>Applies a completed operation only while its original connection is current.</summary>
    internal bool TryApplyAtRevision(long revision, Action apply)
    {
        lock (_notificationSync)
        {
            if (Revision != revision || !IsConnected)
                return false;
            apply();
            return true;
        }
    }

    private void RetireNotifications()
    {
        lock (_notificationSync)
        {
            Interlocked.Increment(ref _revision);
            if (_notificationSource is { } source)
                source.Peer.NotificationReceived -= source.Handler;
            _notificationSource = null;
            _peer = null;
            Session = null;
            SessionChanged?.Invoke(null);
        }
    }

    private void HandleNotification(NotificationSource source, string method, JsonElement payload)
    {
        lock (_notificationSync)
        {
            // An event producer may already have copied its invocation list before unsubscribe.
            if (!ReferenceEquals(_notificationSource, source) || source.Revision != Revision)
                return;
            NotificationReceived?.Invoke(method, payload);
        }
    }

    private sealed class NotificationSource
    {
        internal NotificationSource(DiagnosticsConnection owner, RpcPeer peer, long revision)
        {
            Peer = peer;
            Revision = revision;
            Handler = (method, payload) => owner.HandleNotification(this, method, payload);
        }

        internal RpcPeer Peer { get; }
        internal long Revision { get; }
        internal Action<string, JsonElement> Handler { get; }
    }
}
