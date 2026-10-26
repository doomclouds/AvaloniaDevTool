using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public async Task SetOfficialPropertyAsync(OfficialSetPropertyRequest request,
        CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(OfficialPropertyMethods.Set, request, cancellationToken)
            .ConfigureAwait(false);

    public Task<StyleSnapshot> GetOfficialStylesAsync(OfficialStylesRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<StyleSnapshot>(OfficialPropertyMethods.Styles, request, cancellationToken);
}
