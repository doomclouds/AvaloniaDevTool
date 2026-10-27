namespace AvaloniaDevTool.Protocol;

public static class PropertyValueMethods
{
    public const string Set = "properties.setOfficial";
    public const string Styles = "inspection.stylesOfficial";
}

public sealed record SetPropertyValueRequest(
    long ObjectId,
    string PropertyId,
    string Value,
    bool IsXamlValue = false);

public sealed record InspectStylesRequest(
    long ObjectId,
    string?[]? PropertyNames = null,
    bool IncludeDefaults = false);
