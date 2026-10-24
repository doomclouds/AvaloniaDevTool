using System.Net;
using System.Security.Cryptography;
using System.Text;
using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AvaloniaDevTool.Remote;

/// <summary>Optional authenticated HTTP/WebSocket host for an existing native agent.</summary>
public sealed class DiagnosticsRemoteServer : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly CancellationTokenSource _stopping;
    private int _disposed;
    private DiagnosticsRemoteServer(WebApplication application, CancellationTokenSource stopping, Uri endpoint)
    {
        _application = application;
        _stopping = stopping;
        Endpoint = endpoint;
    }

    public Uri Endpoint { get; }
    public static string GenerateBearerToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static async Task<DiagnosticsRemoteServer> StartAsync(DiagnosticsAgent agent,
        DiagnosticsRemoteServerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(options);
        if (options.BearerToken is not { Length: >= 43 and <= 4096 } ||
            options.BearerToken.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("Use a cryptographically generated base64url token with at least 32 random bytes.", nameof(options));
        ArgumentNullException.ThrowIfNull(options.ListenAddress);
        if (options.Port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "The listening port is outside the allowed range.");
        if (!IPAddress.IsLoopback(options.ListenAddress) && options.ServerCertificate is null)
            throw new ArgumentException("A server certificate is required for a non-loopback listener.", nameof(options));
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var origin in options.AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https") || origin != uri.GetLeftPart(UriPartial.Authority))
                throw new ArgumentException("Allowed origins must be exact HTTP(S) authorities without paths.", nameof(options));
            origins.Add(origin);
        }

        // No ambient URL, logging, launch-profile or command-line configuration may expose a token or widen binding.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxConcurrentUpgradedConnections = 4;
            server.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            server.Listen(options.ListenAddress, options.Port, listen =>
            {
                if (options.ServerCertificate is { } certificate) listen.UseHttps(certificate);
            });
        });
        var application = builder.Build();
        var stopping = new CancellationTokenSource();
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.BearerToken));
        application.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(20)
        });
        application.Run(async context =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Path != "/diagnostics" || context.Request.QueryString.HasValue)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var authorization = context.Request.Headers.Authorization;
            if (authorization.Count != 1 || authorization[0] is not { } header ||
                !header.StartsWith("Bearer ", StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(Encoding.UTF8.GetBytes(header[7..]))))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (context.Request.Headers.TryGetValue("Origin", out var origin) &&
                (origin.Count != 1 || !origins.Contains(origin[0] ?? "")))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var connectionStop = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, context.RequestAborted);
            try
            {
                // Reservation happens before upgrading; failed/busy requests never create AgentSession objects.
                await agent.ServeConnectionAsync(async _ =>
                    new RpcWebSocketStream(await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false)),
                    connectionStop.Token).ConfigureAwait(false);
            }
            catch (RpcException exception) when (exception.Code == "session_busy" && !context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
            }
            catch (OperationCanceledException) when (connectionStop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (connectionStop.IsCancellationRequested) { }
        });
        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var address = new Uri(addresses.Addresses.Single());
            var endpoint = new UriBuilder(address) { Scheme = options.ServerCertificate is null ? "ws" : "wss", Path = "/diagnostics" }.Uri;
            return new DiagnosticsRemoteServer(application, stopping, endpoint);
        }
        catch
        {
            stopping.Dispose();
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stopping.CancelAsync().ConfigureAwait(false);
        await _application.StopAsync().ConfigureAwait(false);
        await _application.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}

