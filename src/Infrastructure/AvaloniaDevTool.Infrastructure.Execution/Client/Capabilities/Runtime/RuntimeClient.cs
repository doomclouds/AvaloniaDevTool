using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class RuntimeClient
{
    private readonly DiagnosticsRpcContext context;

    internal RuntimeClient(DiagnosticsRpcContext context)
    {
        this.context = context;
    }

    public Task<RuntimeObjectSnapshot> GetRuntimeMembersAsync(long objectId,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default) => context.InvokeAsync<RuntimeObjectSnapshot>(
        RuntimeInspectionMethods.Members,
        new RuntimeMembersRequest(objectId, offset, limit),
        cancellationToken);

    public Task<RuntimeItemsSnapshot> GetRuntimeItemsAsync(long objectId,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default) => context.InvokeAsync<RuntimeItemsSnapshot>(
        RuntimeInspectionMethods.Items,
        new RuntimeItemsRequest(objectId, offset, limit),
        cancellationToken);
}
