using System.Net;
using System.Net.WebSockets;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    /// <summary>Connects to a native RPC WebSocket endpoint using an Authorization header.</summary>
    /// <remarks>Plaintext is restricted to literal loopback addresses; normal TLS validation is retained.</remarks>
    public Task ConnectRemoteAsync(Uri endpoint, string bearerToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != "ws" && endpoint.Scheme != "wss") ||
            !string.IsNullOrEmpty(endpoint.Fragment) || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query))
            throw new ArgumentException("A ws/wss endpoint without user information, query or fragment is required.", nameof(endpoint));
        if (endpoint.Scheme == "ws" &&
            !(IPAddress.TryParse(endpoint.DnsSafeHost, out var address) && IPAddress.IsLoopback(address)))
            throw new ArgumentException("Plaintext WebSockets require a literal loopback address; use wss for remote hosts.", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Length > 4096 ||
            bearerToken.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("A base64url bearer token is required.", nameof(bearerToken));
        return ConnectStreamAsync(async token =>
        {
            var socket = new ClientWebSocket();
            if (endpoint.Scheme == "ws") socket.Options.Proxy = null;
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
        }, cancellationToken);
    }
}

