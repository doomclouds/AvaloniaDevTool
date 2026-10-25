using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public async Task SetFollowFocusAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(ExplorationMethods.FollowFocus, new FollowFocusRequest(enabled), cancellationToken).ConfigureAwait(false);

    public Task<IReadOnlyList<TreeNodeInfo>> GetAncestorsAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<TreeNodeInfo>>(ExplorationMethods.Ancestors, new TreeAncestorsRequest(objectId, kind), cancellationToken);

    public Task<TreeSearchResult> SearchTreeAsync(TreeSearchRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<TreeSearchResult>(ExplorationMethods.Search, request, cancellationToken);

    public Task<ElementActionResult> PerformElementActionAsync(long objectId, ElementAction action, CancellationToken cancellationToken = default) =>
        InvokeAsync<ElementActionResult>(ExplorationMethods.Action, new ElementActionRequest(objectId, action), cancellationToken);

    public Task<IReadOnlyList<PseudoClassInfo>> GetPseudoClassesAsync(long objectId, CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<PseudoClassInfo>>(ExplorationMethods.PseudoClasses, new ObjectRequest(objectId), cancellationToken);

    public async Task SetPseudoClassAsync(long objectId, string name, bool enabled, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(ExplorationMethods.SetPseudoClass, new SetPseudoClassRequest(objectId, name, enabled), cancellationToken).ConfigureAwait(false);

    public async Task ResetPseudoClassAsync(long objectId, string name, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(ExplorationMethods.ResetPseudoClass, new ResetPseudoClassRequest(objectId, name), cancellationToken).ConfigureAwait(false);

    public Task<ElementScreenshot> CaptureElementAsync(long objectId, CancellationToken cancellationToken = default) =>
        InvokeAsync<ElementScreenshot>(ExplorationMethods.Screenshot, new ObjectRequest(objectId), cancellationToken);

    public Task<ElementScreenshot> CaptureElementAsync(CaptureElementRequest request, CancellationToken cancellationToken = default) =>
        InvokeAsync<ElementScreenshot>(ExplorationMethods.Screenshot, request, cancellationToken);
}

