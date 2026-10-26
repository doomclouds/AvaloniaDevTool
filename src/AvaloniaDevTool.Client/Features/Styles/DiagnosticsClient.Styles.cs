using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<StyleSnapshot> GetStylesAsync(long objectId, CancellationToken cancellationToken = default) =>
        InvokeAsync<StyleSnapshot>(InspectionMethods.Styles, new ObjectRequest(objectId), cancellationToken);
}
