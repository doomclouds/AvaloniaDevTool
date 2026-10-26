using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Protocol;

public static partial class Methods
{
    public const string TreeRoots = "tree.roots";
    public const string TreeChildren = "tree.children";
    public const string TreeInvalidated = "tree.invalidated";
}

[JsonConverter(typeof(JsonStringEnumConverter<TreeKind>))]
public enum TreeKind
{
    Visual,
    Logical,
    Merged
}

[JsonConverter(typeof(JsonStringEnumConverter<TreeNodeRole>))]
public enum TreeNodeRole
{
    Element,
    TemplateGroup,
    TemplateRoot,
    TemplatePart,
    ContentSlot
}

public sealed record TreeNodeInfo(
    long Id,
    string TypeName,
    string? Name,
    string? Preview,
    bool HasChildren,
    TreeNodeRole Role = TreeNodeRole.Element,
    long? TemplateOwnerId = null,
    long? SlotContentId = null);

public sealed record TreeRootsRequest(TreeKind Kind);

public sealed record TreeChildrenRequest(long ObjectId, TreeKind Kind);

public sealed record TreeInvalidatedNotification(long? ParentId = null);
