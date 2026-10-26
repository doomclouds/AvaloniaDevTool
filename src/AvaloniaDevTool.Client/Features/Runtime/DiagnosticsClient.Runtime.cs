using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<RuntimeObjectSnapshot> GetRuntimeMembersAsync(long objectId,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<RuntimeObjectSnapshot>(RuntimeInspectionMethods.Members,
            new RuntimeMembersRequest(objectId, offset, limit),
            cancellationToken);

    public Task<RuntimeItemsSnapshot> GetRuntimeItemsAsync(long objectId,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<RuntimeItemsSnapshot>(RuntimeInspectionMethods.Items,
            new RuntimeItemsRequest(objectId, offset, limit),
            cancellationToken);
}
