namespace AvaloniaDevTool.Protocol;

public static class OfficialPropertyMethods
{
    public const string Set = "properties.setOfficial";
    public const string Styles = "inspection.stylesOfficial";
}

public sealed record OfficialSetPropertyRequest(long ObjectId, string PropertyId, string Value, bool IsXamlValue = false);
public sealed record OfficialStylesRequest(long ObjectId, string?[]? PropertyNames = null, bool IncludeDefaults = false);
