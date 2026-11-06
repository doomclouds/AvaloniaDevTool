using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class PropertiesClient
{
    private readonly DiagnosticsRpcContext context;

    internal PropertiesClient(DiagnosticsRpcContext context)
    {
        this.context = context;
        context.Subscribe((method, payload) =>
        {
            switch (method)
            {
                case Methods.PropertiesChanged:
                    PropertiesChanged?.Invoke(ProtocolJson.Read<PropertiesChangedNotification>(payload));
                    return true;
                default:
                    return false;
            }
        });
    }

    public event Action<PropertiesChangedNotification>? PropertiesChanged;

    public Task<IReadOnlyList<PropertyInfoDto>>
        GetPropertiesAsync(long objectId, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<PropertyInfoDto>>(Methods.PropertiesGet,
            new ObjectRequest(objectId),
            cancellationToken);

    public async Task SetPropertyAsync(long objectId,
        string propertyId,
        string value,
        CancellationToken cancellationToken = default) => _ = await context
        .InvokeAsync<OperationResult>(Methods.PropertiesSet,
            new SetPropertyRequest(objectId, propertyId, value),
            cancellationToken).ConfigureAwait(false);

    public async Task
        ClearPropertyAsync(long objectId, string propertyId, CancellationToken cancellationToken = default) => _ =
        await context.InvokeAsync<OperationResult>(Methods.PropertiesClear,
            new ClearPropertyRequest(objectId, propertyId),
            cancellationToken).ConfigureAwait(false);

    public Task<OperationResult>
        SetBrushAsync(SetBrushRequest request, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<OperationResult>(BrushMethods.Set, request, cancellationToken);

    public async Task SetOfficialPropertyAsync(SetPropertyValueRequest request,
        CancellationToken cancellationToken = default) => _ = await context
        .InvokeAsync<OperationResult>(PropertyValueMethods.Set, request, cancellationToken).ConfigureAwait(false);

    public Task<StyleSnapshot> GetOfficialStylesAsync(InspectStylesRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<StyleSnapshot>(PropertyValueMethods.Styles, request, cancellationToken);

    public Task<IReadOnlyList<PropertyInfoDto>> GetPropertiesAsync(PropertiesRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<PropertyInfoDto>>(Methods.PropertiesGet, request, cancellationToken);

    public Task<IReadOnlyList<PropertyInfoDto>>
        GetPropertiesIncludingClrAsync(long objectId, CancellationToken cancellationToken = default) =>
        context.InvokeAsync<IReadOnlyList<PropertyInfoDto>>(Methods.PropertiesGet,
            new PropertiesRequest(objectId, true),
            cancellationToken);

    public Task<PropertyPreviewResult> GetPropertyPreviewAsync(PropertyPreviewRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<PropertyPreviewResult>(PropertyPresentationMethods.Preview, request, cancellationToken);
}
