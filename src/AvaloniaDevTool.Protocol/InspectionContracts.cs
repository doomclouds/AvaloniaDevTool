namespace AvaloniaDevTool.Protocol;

public static class InspectionMethods
{
    public const string Styles = "inspection.styles";
    public const string Resources = "inspection.resources";
    public const string ResourceEntries = "inspection.resourceEntries";
    public const string SetResource = "inspection.setResource";
    public const string ResetResource = "inspection.resetResource";
    public const string Assets = "inspection.assets";
    public const string OpenAsset = "inspection.openAsset";
}

public sealed record StyleSetterInfo(string PropertyName, string DeclaringType, string? Value, bool? MatchesEffectiveValue);
public sealed record StyleFrameInfo(string Source, string Kind, string Priority, bool IsActive, IReadOnlyList<StyleSetterInfo> Setters);
public sealed record BindingDiagnosticInfo(string PropertyName, string Expression, string ExpressionType, string ErrorType,
    bool? IsRunning, string Priority, string? Diagnostic, BindingDetails? Details = null);
public sealed record StyleSnapshot(IReadOnlyList<StyleFrameInfo> Frames, IReadOnlyList<BindingDiagnosticInfo> Bindings,
    IReadOnlyList<string> ValidationErrors, string Adapter, bool Truncated = false, IReadOnlyList<string>? Limitations = null);
public sealed record ResourcesRequest(long? ObjectId = null);
public sealed record ResourceDictionaryInfo(long Id, string Name, string Scope, string? Theme, int EntryCount,
    IReadOnlyList<ResourceDictionaryInfo> Children);
public sealed record ResourceEntriesRequest(long DictionaryId);
public sealed record ResourceEntryInfo(string Key, string KeyType, string TypeName, string? Value, bool CanEdit, bool IsOverridden);
public sealed record SetResourceRequest(long DictionaryId, string Key, string Value);
public sealed record ResetResourceRequest(long DictionaryId, string Key);
public sealed record AssetInfo(string Uri, string AssemblyName, string Extension);
public sealed record OpenAssetRequest(string Uri);
public sealed record AssetPreview(string Uri, string MediaType, string? Text, string? Base64, int ByteLength, bool IsTruncated,
    long? TotalByteLength = null, AssetImageMetadata? Image = null, AssetFontMetadata? Font = null, string? PreviewNotice = null);

