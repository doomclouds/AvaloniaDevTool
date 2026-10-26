using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Protocol;

public static class ExplorationMethods
{
    public const string Ancestors = "tree.ancestors";
    public const string Search = "tree.search";
    public const string Action = "element.action";
    public const string PseudoClasses = "element.pseudoclasses";
    public const string SetPseudoClass = "element.pseudoclass.set";
    public const string ResetPseudoClass = "element.pseudoclass.reset";
    public const string Screenshot = "element.screenshot";
    public const string FollowFocus = "selection.followFocus";
}

public sealed record TreeAncestorsRequest(long ObjectId, TreeKind Kind);

public sealed record TreeSearchRequest(
    string Query,
    TreeKind Kind,
    long? RootId = null,
    int MaxResults = 100,
    int MaxVisited = 3000);

public sealed record TreeSearchResult(IReadOnlyList<TreeNodeInfo> Matches, int Visited, bool Truncated);

[JsonConverter(typeof(JsonStringEnumConverter<ElementAction>))]
public enum ElementAction
{
    Focus,
    BringIntoView,
    Enable,
    Disable
}

public sealed record ElementActionRequest(long ObjectId, ElementAction Action);

public sealed record ElementActionResult(bool Succeeded, string? Detail = null);

public sealed record PseudoClassInfo(string Name, bool IsActive, bool IsDeclared, bool HasOverride);

public sealed record SetPseudoClassRequest(long ObjectId, string Name, bool Enabled);

public sealed record ResetPseudoClassRequest(long ObjectId, string Name);

public sealed record ScreenshotBounds(double X, double Y, double Width, double Height);

public sealed record CaptureElementRequest(long ObjectId, ScreenshotBounds? Clip = null, double Scale = 1);

public sealed record ElementScreenshot(
    int Width,
    int Height,
    string PngBase64,
    ScreenshotBounds? Bounds = null,
    double Scale = 1,
    double DpiX = 96,
    double DpiY = 96);

public sealed record FollowFocusRequest(bool Enabled);
