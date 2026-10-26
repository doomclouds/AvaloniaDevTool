using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<IReadOnlyList<PseudoClassInfo>> GetPseudoClassesAsync(long objectId,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<PseudoClassInfo>>(ExplorationMethods.PseudoClasses,
            new ObjectRequest(objectId),
            cancellationToken);

    public async Task SetPseudoClassAsync(long objectId,
        string name,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(ExplorationMethods.SetPseudoClass,
            new SetPseudoClassRequest(objectId, name, enabled),
            cancellationToken).ConfigureAwait(false);

    public async Task ResetPseudoClassAsync(long objectId,
        string name,
        CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(ExplorationMethods.ResetPseudoClass,
            new ResetPseudoClassRequest(objectId, name),
            cancellationToken).ConfigureAwait(false);
}
