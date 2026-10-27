using System.Collections.Concurrent;
using System.Net;
using AvaloniaDevTool.Cli.Catalog;
using AvaloniaDevTool.Cli.Hosting.Options;
using AvaloniaDevTool.Cli.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli.Hosting.Http;

/// <summary>Runs the complete tool catalog beside its target applications over LAN MCP.</summary>
public static class HttpMcpHost
{
    public static async Task<int> RunAsync(McpHostOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Role != "serve")
            throw new ArgumentException("The HTTP host requires the http transport.", nameof(options));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(settings => settings.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = 8 * 1024 * 1024;
            server.Listen(options.ListenAddress, options.Port);
        });

        await using var sessions = new HttpSessionRegistry(options.PipeName);
        builder.Services.AddMcpServer().WithHttpTransport(transport =>
        {
            transport.SessionMode = HttpServerSessionMode.Stateful;
            transport.ConfigureSessionOptions = sessions.ConfigureAsync;
            // This narrowly scoped SDK hook gives our state the actual MCP-session lifetime.
#pragma warning disable MCPEXP002
            transport.RunSessionHandler = sessions.RunAsync;
#pragma warning restore MCPEXP002
        });

        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Path != McpHostOptions.EndpointPath)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!IsExpectedHost(context.Request.Host, options) || context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (context.Request.Headers.TryGetValue("Mcp-Session-Id", out var sessionId) &&
                (sessionId.Count != 1 || !sessions.IsActive(sessionId[0]!)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        app.MapMcp(McpHostOptions.EndpointPath);
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var address = new UriBuilder("http",
                options.ListenAddress.ToString(),
                options.Port,
                McpHostOptions.EndpointPath).Uri;
            Console.Error.WriteLine($"AvaloniaDevTool HTTP MCP listening at {address} (no token).");
            if (options.ListenAddress.Equals(IPAddress.Any) || options.ListenAddress.Equals(IPAddress.IPv6Any))
                Console.Error.WriteLine("Use this computer's reachable IP in the Codex MCP url configuration.");
        });
        await app.RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static bool IsExpectedHost(HostString host, McpHostOptions options)
    {
        if ((host.Port ?? 80) != options.Port)
            return false;
        if (host.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.IsLoopback(options.ListenAddress);
        if (!IPAddress.TryParse(host.Host.Trim('[', ']'), out var address))
            return false;
        return options.ListenAddress.Equals(IPAddress.Any) || options.ListenAddress.Equals(IPAddress.IPv6Any) ||
               options.ListenAddress.Equals(address);
    }

    private sealed class HttpSessionRegistry(string? initialPipe) : IAsyncDisposable
    {
        private const int MaximumSessions = 64;
        private readonly NodeGenerationAllocator _generations = new();
        private readonly DesktopControlLease _desktop = new();

        private readonly ConcurrentDictionary<McpServerOptions, HttpToolSession> _pending =
            new(ReferenceEqualityComparer.Instance);

        private readonly ConcurrentDictionary<string, HttpToolSession> _active = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _stop = new();
        private int _count;
        private int _disposed;

        public async Task ConfigureAsync(HttpContext _, McpServerOptions options, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _count) > MaximumSessions)
            {
                Interlocked.Decrement(ref _count);
                throw new InvalidOperationException("session_limit: The HTTP host has reached its session capacity.");
            }

            var state = new HttpToolSession(initialPipe, _generations, _desktop);
            try
            {
                var tools = McpToolCatalog.CreateTools(state.Session, state.Context);
                McpToolCatalog.Configure(options, tools);
                state.ConfigureFilters(options);
                if (!_pending.TryAdd(options, state))
                    throw new InvalidOperationException("session_conflict: The MCP options are already associated.");
            }
            catch
            {
                Interlocked.Decrement(ref _count);
                await state.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public bool IsActive(string sessionId) =>
            _active.TryGetValue(sessionId, out var state) && !state.Expired.IsCancellationRequested;

        public async Task RunAsync(HttpContext httpContext, McpServer server, CancellationToken sessionClosed)
        {
            if (!_pending.TryRemove(server.ServerOptions, out var state))
                throw new InvalidOperationException(
                    "session_missing: No application state was created for the MCP session.");
            var sessionId = server.SessionId;
            using var lifetime =
                CancellationTokenSource.CreateLinkedTokenSource(sessionClosed, state.Expired, _stop.Token);
            var monitor = state.MonitorIdleAsync(lifetime.Token);
            try
            {
                if (string.IsNullOrEmpty(sessionId) || !_active.TryAdd(sessionId, state))
                    throw new InvalidOperationException(
                        "session_missing: Stateful MCP did not provide a unique session identifier.");
                await server.RunAsync(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            finally
            {
                if (sessionId is not null)
                    _active.TryRemove(sessionId, out _);
                try
                {
                    await lifetime.CancelAsync().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        try
                        {
                            await monitor.ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                        {
                        }
                    }
                    finally
                    {
                        try
                        {
                            await state.DisposeAsync().ConfigureAwait(false);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _count);
                        }
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            try
            {
                await _stop.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                var states = _active.Values.Concat(_pending.Values).Distinct().ToArray();
                _active.Clear();
                _pending.Clear();
                try
                {
                    await Task.WhenAll(states.Select(state => state.DisposeAsync().AsTask())).ConfigureAwait(false);
                }
                finally
                {
                    _stop.Dispose();
                }
            }
        }
    }
}
