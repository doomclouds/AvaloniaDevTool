namespace AvaloniaDevTool.Protocol;

public static class TreeWorkflowMethods
{
    public const string Observe = "tree.observe";
    public const string Release = "tree.release";
    public const string Selector = "tree.selector";
    public const string Metadata = "tree.metadata";
}

public sealed record TreeObserveRequest(long ObjectId, TreeKind Kind);
public sealed record TreeReleaseRequest(IReadOnlyList<long> ObjectIds, TreeKind Kind);
public sealed record TreeObservationResult(int ObservedBranches);
public sealed record ElementSelectorResult(bool Supported, string? Selector, bool IsUnique, int MatchCount,
    bool ValidationTruncated, string? Detail = null);
public sealed record ElementMetadata(string SessionId, string BackendId, long ObjectId, string TypeName,
    string? Name, IReadOnlyList<string> Classes, ElementSelectorResult Selector);

public sealed record TreeExpansionOptions(int MaxNodes = 1000, int MaxDepth = 32, int BatchSize = 32,
    bool IncludeTemplates = true);
public sealed record TreeExpansionResult(int Visited, int Expanded, bool Truncated, bool Cancelled);
