using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public Task<TreeChildrenPage> GetChildrenPageAsync(long objectId, TreeKind kind, int offset = 0, int count = 100,
        string? cursor = null, CancellationToken cancellationToken = default) =>
        InvokeAsync<TreeChildrenPage>(TreePagingMethods.Children, new TreeChildrenPageRequest(objectId, kind, offset, count, cursor), cancellationToken);

    public Task<TemplateParentSelectorResult> GetTemplateParentSelectorAsync(long objectId, CancellationToken cancellationToken = default) =>
        InvokeAsync<TemplateParentSelectorResult>(TreePagingMethods.TemplateParentSelector, new ObjectRequest(objectId), cancellationToken);
}
