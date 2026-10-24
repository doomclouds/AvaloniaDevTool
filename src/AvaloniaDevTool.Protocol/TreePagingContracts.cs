namespace AvaloniaDevTool.Protocol;

public static class TreePagingMethods
{
    public const string Children = "tree.children-page";
    public const string TemplateParentSelector = "tree.template-parent-selector";
}

public sealed record TreeChildrenPageRequest(long ObjectId, TreeKind Kind, int Offset = 0, int Count = 100,
    string? Cursor = null);
public sealed record TreeChildrenPage(IReadOnlyList<TreeNodeInfo> Children, int Offset, int TotalCount,
    bool HasMore, string? NextCursor);
public sealed record TemplateParentSelectorResult(long ObjectId, long? TemplateParentId, ElementSelectorResult Selector);
