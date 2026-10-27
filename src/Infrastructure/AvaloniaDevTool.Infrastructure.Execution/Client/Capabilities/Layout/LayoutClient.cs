using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class LayoutClient
{
    private readonly DiagnosticsRpcContext context;

    internal LayoutClient(DiagnosticsRpcContext context)
    {
        this.context = context;
    }

    public Task<LayoutSnapshot> GetLayoutSnapshotAsync(long objectId,
        int maxNodes = 128,
        int maxDepth = 12,
        CancellationToken cancellationToken = default) => context.InvokeAsync<LayoutSnapshot>(LayoutMethods.Snapshot,
        new LayoutSnapshotRequest(objectId, maxNodes, maxDepth),
        cancellationToken);
}
