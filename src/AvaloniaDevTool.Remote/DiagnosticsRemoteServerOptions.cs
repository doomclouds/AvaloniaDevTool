using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace AvaloniaDevTool.Remote;

public sealed class DiagnosticsRemoteServerOptions
{
    public required string BearerToken { get; init; }
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;
    public int Port { get; init; }
    public X509Certificate2? ServerCertificate { get; init; }
    /// <summary>Exact HTTP(S) origins. Requests containing Origin are denied by default.</summary>
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
}
