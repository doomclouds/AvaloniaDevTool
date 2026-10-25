using System.IO.Pipes;
using System.Text.Json;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

/// <summary>Owns one native diagnostics session and its notification subscriptions.</summary>
public sealed partial class DiagnosticsClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private RpcPeer? _peer;
    private int _disposed;
    public bool IsConnected => _peer is not null && Session is not null;
    public SessionInfo? Session { get; private set; }
    public RpcTraceBuffer ProtocolTrace { get; } = new();
    public event Action? ConnectionChanged;
    public event Action<SelectionChangedNotification>? SelectionChanged;
    public event Action<TreeInvalidatedNotification>? TreeInvalidated;
    public event Action<PropertiesChangedNotification>? PropertiesChanged;
    public event Action<PickingChangedNotification>? PickingChanged;

    public Task ConnectAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        return ConnectStreamAsync(async token =>
        {
            var stream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
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
        }, cancellationToken);
    }

    private async Task ConnectStreamAsync(Func<CancellationToken, Task<Stream>> streamFactory, CancellationToken cancellationToken)
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
                peer.NotificationReceived += HandleNotification;
                peer.Start();
                var info = await peer.InvokeAsync<SessionInfo>(Methods.SessionInfo, null, connectTimeout.Token).ConfigureAwait(false);
                if (info.ProtocolVersion != DiagnosticsProtocol.Version)
                    throw new RpcException("unsupported_version", $"Protocol {info.ProtocolVersion} is not supported by this client.");
                Session = info;
                UpdateContentGeneration(info.ContentGeneration);
                _peer = peer;
                RaiseConnectionChanged();
                _ = ObserveDisconnectionAsync(peer);
            }
            catch
            {
                if (peer is not null)
                    await peer.DisposeAsync().ConfigureAwait(false);
                else if (stream is not null)
                    await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public Task<IReadOnlyList<TreeNodeInfo>> GetRootsAsync(TreeKind kind, CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<TreeNodeInfo>>(Methods.TreeRoots, new TreeRootsRequest(kind), cancellationToken);
    public Task<IReadOnlyList<TreeNodeInfo>> GetChildrenAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<TreeNodeInfo>>(Methods.TreeChildren, new TreeChildrenRequest(objectId, kind), cancellationToken);
    public Task<IReadOnlyList<PropertyInfoDto>> GetPropertiesAsync(long objectId, CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<PropertyInfoDto>>(Methods.PropertiesGet, new ObjectRequest(objectId), cancellationToken);
    public async Task SetPropertyAsync(long objectId, string propertyId, string value, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(Methods.PropertiesSet, new SetPropertyRequest(objectId, propertyId, value), cancellationToken).ConfigureAwait(false);
    public async Task ClearPropertyAsync(long objectId, string propertyId, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(Methods.PropertiesClear, new ClearPropertyRequest(objectId, propertyId), cancellationToken).ConfigureAwait(false);
    public async Task HighlightAsync(long? objectId, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(Methods.Highlight, new HighlightRequest(objectId), cancellationToken).ConfigureAwait(false);
    public async Task SetPickingAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(Methods.SetPicking, new PickingRequest(enabled), cancellationToken).ConfigureAwait(false);

    private Task<T> InvokeAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
    {
        var peer = _peer ?? throw new RpcException("not_connected", "Connect to a diagnostic application first.");
        return InvokeDiagnosticMutation(method, () => peer.InvokeAsync<T>(method, parameters, cancellationToken));
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    private async Task DisconnectCoreAsync()
    {
        var peer = _peer;
        _peer = null;
        Session = null;
        Interlocked.Exchange(ref _contentGeneration, 0);
        if (peer is not null)
        {
            peer.NotificationReceived -= HandleNotification;
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
        finally { _lifecycle.Release(); }
    }

    private void HandleNotification(string method, JsonElement payload)
    {
        if (HandlePreviewNotification(method, payload)) return;
        if (HandleEventRouteNotification(method, payload)) return;
        if (HandleBreakpointNotification(method, payload)) return;
        switch (method)
        {
            case MetricsMethods.SampleAdded: HandleMetricsSamplesAdded(payload); break;
            case ProfilerMethods.SamplesAdded: HandleProfilerSamplesAdded(payload); break;
            case ActivityMethods.EntryAdded: ActivityEntryAdded?.Invoke(ProtocolJson.Read<ActivityEntry>(payload)); break;
            case ActivityMethods.StateChanged: ActivityStateChanged?.Invoke(ProtocolJson.Read<ActivityStatus>(payload)); break;
            case Methods.SelectionChanged: SelectionChanged?.Invoke(ProtocolJson.Read<SelectionChangedNotification>(payload)); break;
            case Methods.TreeInvalidated: TreeInvalidated?.Invoke(ProtocolJson.Read<TreeInvalidatedNotification>(payload)); break;
            case Methods.PropertiesChanged: PropertiesChanged?.Invoke(ProtocolJson.Read<PropertiesChangedNotification>(payload)); break;
            case Methods.PickingChanged: PickingChanged?.Invoke(ProtocolJson.Read<PickingChangedNotification>(payload)); break;
        }
    }

    private void RaiseConnectionChanged()
    {
        if (ConnectionChanged is not { } handlers)
            return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await DisconnectAsync().ConfigureAwait(false);
    }
}

