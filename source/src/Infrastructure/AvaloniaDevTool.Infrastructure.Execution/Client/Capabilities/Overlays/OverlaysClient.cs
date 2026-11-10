using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class OverlaysClient
{
    private readonly DiagnosticsRpcContext context;

    internal OverlaysClient(DiagnosticsRpcContext context)
    {
        this.context = context;
    }

    public Task<OverlaySnapshot> ConfigureOverlayAsync(OverlayOptions options,
        long? objectId = null,
        CancellationToken cancellationToken = default) => context.InvokeAsync<OverlaySnapshot>(OverlayMethods.Configure,
        new OverlayConfigureRequest(options, objectId),
        cancellationToken);

    public Task<OverlaySnapshot>
        GetOverlaySnapshotAsync(long? objectId = null, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<OverlaySnapshot>(OverlayMethods.Snapshot,
            new OverlaySnapshotRequest(objectId),
            cancellationToken);
}
