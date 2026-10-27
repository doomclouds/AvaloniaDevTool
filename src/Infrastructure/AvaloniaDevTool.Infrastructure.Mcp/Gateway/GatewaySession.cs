using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaDevTool.Cli.Hosting.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli.Gateway;

public sealed record MachineSelection(string Machine, string Mode, long Generation, string State);

/// <summary>One external stdio session. Switching never reconnects or replays a business operation.</summary>
internal sealed class GatewaySession : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _selection = new(1, 1);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly GatewayHandleRegistry _handles = new();
    private readonly string? _initialPipe;
    private readonly IReadOnlyList<McpServerTool> _catalog;
    private readonly NativeOperationTracker _pendingOperations = new();
    private IMcpExecutor _executor;
    private int _active;
    private bool _changingTarget;
    private bool _switching;
    private bool _disconnected;
    private bool _disposed;
    private long _generation = 1;

    internal GatewaySession(string? initialPipe)
    {
        _initialPipe = initialPipe;
        var local = new LocalExecutor(initialPipe);
        _executor = local;
        _catalog = local.Tools;
    }

    internal McpServerTool[] CreateTools()
    {
        var selection = McpServerTool.Create(GetType().GetMethod(nameof(SelectMachine))!, this);
        return _catalog.Select(tool => (McpServerTool)new GatewayTool(tool, this)).Append(selection).ToArray();
    }

    [McpServerTool(Name = "select-machine", ReadOnly = false, Destructive = false),
     Description(
         "Selects the execution computer. Omit ip for this computer; supply any IP (including 127.0.0.1) to select a computer running AvaloniaDevTool serve. Switching requires no active calls or pending native actions, retires old references and disconnects the previous target. Discover and attach an application after a switch. Failed selection keeps the previous machine; lost remote calls never fall back locally or replay actions.")]
    public async Task<MachineSelection> SelectMachine(string? ip = null,
        CancellationToken cancellationToken = default)
    {
        // Deployment/test maintenance override; the tool contract deliberately exposes only IP.
        var configuredPort = Environment.GetEnvironmentVariable("AVDT_EXECUTOR_PORT");
        var port = 7443;
        if (configuredPort is not null && (!int.TryParse(configuredPort, out port) || port is < 1 or > 65535))
            throw new McpException("invalid_executor_configuration: AVDT_EXECUTOR_PORT must be 1..65535.");
        Uri? endpoint = null;
        if (ip is not null)
        {
            IPAddress address;
            try
            {
                address = McpHostOptions.Parse(["serve", "--listen", ip]).ListenAddress;
            }
            catch (McpHostOptionsException)
            {
                throw new McpException(
                    "invalid_machine: Specify a plain IPv4 or IPv6 address, not a host name or URL.");
            }

            endpoint = new UriBuilder("http", address.ToString(), port, "/mcp").Uri;
        }

        await _selection.WaitAsync(cancellationToken).ConfigureAwait(false);
        IMcpExecutor? candidate = null;
        try
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_active != 0 || _pendingOperations.HasPending)
                    throw new McpException(
                        "machine_busy: Finish active calls and pending native actions before switching.");
                var wanted = endpoint?.AbsoluteUri ?? "local";
                if (!_disconnected && _executor.Machine == wanted)
                    return Selection();
            }

            // Preparation only initializes MCP and checks the catalog. It does not attach or modify a target.
            candidate = endpoint is null
                ? new LocalExecutor(_initialPipe)
                : await HttpExecutor.ConnectAsync(endpoint, _catalog, cancellationToken).ConfigureAwait(false);
            IMcpExecutor previous;
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_active != 0 || _pendingOperations.HasPending)
                    throw new McpException(
                        "machine_busy: The current machine became busy while preparing the candidate.");
                _switching = true;
                previous = _executor;
            }

            try
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                lock (_sync)
                {
                    _handles.RetireMachine();
                    _disconnected = true;
                    _switching = false;
                }

                throw new McpException(
                    "machine_cleanup_failed: Previous target cleanup did not complete; explicitly select a machine again.");
            }

            lock (_sync)
            {
                _executor = candidate;
                candidate = null;
                _generation++;
                _handles.RetireMachine();
                _pendingOperations.Clear();
                _disconnected = false;
                _switching = false;
                return Selection();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException(
                "machine_connection_timeout: The candidate did not initialize within ten seconds; the previous selection is unchanged.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw new McpException(
                $"machine_connection_failed: The candidate could not be opened; the previous selection is unchanged. {error.Message}");
        }
        finally
        {
            if (candidate is not null)
                await candidate.DisposeAsync().ConfigureAwait(false);
            _selection.Release();
        }
    }

    private MachineSelection Selection() => new(
        _executor is LocalExecutor ? "local" : new Uri(_executor.Machine).Host.Trim('[', ']'),
        _executor is LocalExecutor ? "local" : "remote",
        _generation,
        _disconnected ? "disconnected" : "ready");

    internal async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken token)
    {
        var parameters = request.Params ?? throw new McpException("invalid_arguments: Missing tool request.");
        var changing = parameters.Name is "attach-to-file" or "detach" ||
                       parameters.Name == "attach-to-app" && parameters.Arguments is { } values &&
                       values.TryGetValue("id", out var id) && id.ValueKind == JsonValueKind.Number &&
                       id.GetInt64() != 0;
        IMcpExecutor executor;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_switching || _changingTarget || changing && (_active != 0 || _pendingOperations.HasPending))
                throw new McpException(
                    "machine_busy: A target or machine transition conflicts with active operations.");
            if (_disconnected)
                throw new McpException(
                    "machine_disconnected: Explicitly select the machine again; the previous operation was not replayed.");
            executor = _executor;
            _active++;
            _changingTarget = changing;
        }

        CallToolRequestParams? mapped = null;
        try
        {
            mapped = _handles.MapArguments(parameters);
            var result = await executor.InvokeAsync(request, mapped, token).ConfigureAwait(false);
            if (parameters.Name == "native-action-status" && result.IsError == true)
                ForgetFailedOperation();
            var mappedResult = _handles.MapResult(parameters.Name, result, out var nativeScopeChanged);
            if (nativeScopeChanged)
                _pendingOperations.Clear();
            _pendingOperations.Observe(parameters.Name, result);
            return mappedResult;
        }
        catch (ExecutorDisconnectedException error)
        {
            lock (_sync)
            {
                _disconnected = true;
                _handles.RetireMachine();
                _pendingOperations.Clear();
            }

            throw new McpException(
                $"execution_unknown: The selected executor disconnected. This operation may have executed; it was not retried. Explicitly select the machine again and inspect its state. {error.InnerException?.Message}");
        }
        catch (McpException) when (parameters.Name == "native-action-status")
        {
            ForgetFailedOperation();
            throw;
        }
        finally
        {
            lock (_sync)
            {
                _active--;
                if (changing)
                    _changingTarget = false;
                if (_disposed && _active == 0)
                    _drained.TrySetResult();
            }
        }

        void ForgetFailedOperation()
        {
            if (mapped?.Arguments is { } arguments && arguments.TryGetValue("operationId", out var operation))
                _pendingOperations.Forget(operation.GetString());
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_active == 0)
                _drained.TrySetResult();
        }

        await _selection.WaitAsync().ConfigureAwait(false);
        try
        {
            await _drained.Task.ConfigureAwait(false);
            await _executor.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _selection.Release();
        }
    }

    private sealed class GatewayTool(McpServerTool metadata, GatewaySession gateway) : DelegatingMcpServerTool(metadata)
    {
        public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
            CancellationToken cancellationToken = default) => gateway.InvokeAsync(request, cancellationToken);
    }
}
