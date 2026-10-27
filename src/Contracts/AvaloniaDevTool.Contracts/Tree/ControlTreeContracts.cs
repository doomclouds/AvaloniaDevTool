namespace AvaloniaDevTool.Protocol;

public static class ControlTreeMethods
{
    public const string Snapshot = "tree.officialSnapshot";
    public const string Ancestors = "tree.officialAncestors";
    public const string Search = "tree.officialSearch";
}

public sealed record ControlTreeRequest(long ObjectId = 0, TreeKind Kind = TreeKind.Logical, int Depth = 10);

public sealed record ControlSearchRequest(
    string Query,
    long ScopeObjectId = 0,
    bool SearchTemplates = false,
    int Limit = -1);

// A flat representation keeps arbitrarily deep trees independent of the JSON nesting limit.
public sealed record ControlTreeEntry(TreeNodeInfo Node, long? ParentId, int Depth);

public sealed record ControlTreeSnapshot(IReadOnlyList<ControlTreeEntry> Entries, int Visited);
