using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private readonly MergedTree _mergedTree = new();

    private void RegisterTreeMethods()
    {
        Map<TreeRootsRequest>(Methods.TreeRoots, request => GetRoots(request.Kind));
        Map<TreeChildrenRequest>(Methods.TreeChildren, request => GetChildren(request));
        Map<TreeChildrenPageRequest>(TreePagingMethods.Children, request => GetChildrenPage(request));
        Map<ObjectRequest>(TreePagingMethods.TemplateParentSelector,
            request => GetTemplateParentSelector(request.ObjectId));
        Map<TreeObserveRequest>(TreeWorkflowMethods.Observe, request => ObserveTree(request));
        Map<TreeReleaseRequest>(TreeWorkflowMethods.Release, request => ReleaseTree(request));
        Map<ObjectRequest>(TreeWorkflowMethods.Selector, request => GetElementSelector(request.ObjectId));
        Map<ObjectRequest>(TreeWorkflowMethods.Metadata, request => GetElementMetadata(request.ObjectId));
    }

    private IReadOnlyList<TreeNodeInfo> GetRoots(TreeKind kind)
    {
        ValidateTreeKind(kind);
        return RefreshRoots().Select(x => Describe(x, kind)).ToArray();
    }

    private IReadOnlyList<TreeNodeInfo> GetChildren(TreeChildrenRequest request)
    {
        ValidateTreeKind(request.Kind);
        var parent = _objects.Get(request.ObjectId);
        if (request.Kind == TreeKind.Merged)
            MergedPath(parent, RefreshRoots());
        var children = ChildObjects(parent, request.Kind).ToArray();
        ObserveTree(new TreeObserveRequest(request.ObjectId, request.Kind));
        return children.Select(x => Describe(x, request.Kind)).ToArray();
    }

    private TreeNodeInfo Describe(object value, TreeKind kind)
    {
        var id = Observe(value);
        if (kind == TreeKind.Merged)
        {
            var metadata = _mergedTree.Metadata(value);
            if (value is MergedTree.SyntheticNode synthetic)
                return new TreeNodeInfo(id,
                    value.GetType().FullName!,
                    synthetic.Label,
                    null,
                    ChildObjects(value, kind).Any(),
                    metadata.Role,
                    metadata.Owner is { } owner ? Observe(owner) : null,
                    metadata.Content is { } content ? Observe(content) : null);
        }

        var name = (value as StyledElement)?.Name;
        var preview = value switch
        {
            TextBlock text => text.Text,
            TextBox text => text.Text,
            ContentControl { Content: string text } => text,
            Window window => window.Title,
            _ => null
        };
        if (preview?.Length > 100)
            preview = preview[..100] + "…";
        var details = kind == TreeKind.Merged ? _mergedTree.Metadata(value) : default;
        return new TreeNodeInfo(id,
            value.GetType().FullName ?? value.GetType().Name,
            name,
            preview,
            ChildObjects(value, kind).Any(),
            details.Role,
            details.Owner is { } templateOwner ? Observe(templateOwner) : null);
    }

    private IEnumerable<object> ChildObjects(object parent, TreeKind kind)
    {
        IEnumerable<object> children = kind switch
        {
            TreeKind.Visual when parent is Visual visual => visual.GetVisualChildren(),
            TreeKind.Logical when parent is ILogical logical => logical.LogicalChildren,
            TreeKind.Merged => _mergedTree.Children(parent),
            _ => []
        };
        return children.Where(x => !OverlayService.IsDiagnosticVisual(x));
    }
}
