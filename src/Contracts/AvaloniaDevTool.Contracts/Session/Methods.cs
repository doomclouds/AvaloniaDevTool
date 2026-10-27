namespace AvaloniaDevTool.Protocol;

/// <summary>Method names for the basic diagnostic RPC contract.</summary>
public static class Methods
{
    public const string PropertiesGet = "properties.get";
    public const string PropertiesSet = "properties.set";
    public const string PropertiesClear = "properties.clear";
    public const string PropertiesChanged = "properties.changed";

    public const string Highlight = "selection.highlight";
    public const string SetPicking = "selection.pick";
    public const string SelectionChanged = "selection.changed";
    public const string PickingChanged = "picking.changed";

    public const string SessionInfo = "session.info";

    public const string TreeRoots = "tree.roots";
    public const string TreeChildren = "tree.children";
    public const string TreeInvalidated = "tree.invalidated";
}
