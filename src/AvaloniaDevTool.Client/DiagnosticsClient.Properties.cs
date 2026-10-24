using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<IReadOnlyList<PropertyInfoDto>> GetPropertiesAsync(PropertiesRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<PropertyInfoDto>>(Methods.PropertiesGet, request, cancellationToken);
    public Task<IReadOnlyList<PropertyInfoDto>> GetPropertiesIncludingClrAsync(long objectId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<PropertyInfoDto>>(Methods.PropertiesGet, new PropertiesRequest(objectId, true), cancellationToken);

    public Task<PropertyPreviewResult> GetPropertyPreviewAsync(PropertyPreviewRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<PropertyPreviewResult>(PropertyPresentationMethods.Preview, request, cancellationToken);
}
