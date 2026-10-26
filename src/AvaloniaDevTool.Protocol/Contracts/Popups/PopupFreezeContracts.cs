namespace AvaloniaDevTool.Protocol;

public static class PopupFreezeMethods
{
    public const string Status = "popup-freeze/status";
    public const string Set = "popup-freeze/set";
    public const string Resume = "popup-freeze/resume";
}

/// <summary>A null root selects all configured roots, including roots added while frozen.</summary>
public sealed record PopupFreezeSetRequest(bool IsFrozen, long? RootId = null);

public sealed record PopupFreezeRootDto(long RootId, string Title, bool IsFrozen);

public sealed record PopupFreezePopupDto(
    long PopupId,
    long RootId,
    string Kind,
    bool IsUsingOverlayLayer,
    bool IsLightDismissEnabled,
    bool HasOverride,
    string? Limitation);

public sealed record PopupFreezeStatusDto(
    bool IsActive,
    bool AllRoots,
    IReadOnlyList<PopupFreezeRootDto> Roots,
    IReadOnlyList<PopupFreezePopupDto> Popups,
    IReadOnlyList<string> Conflicts,
    string Source = "Avalonia public Popup.IsLightDismissEnabled / OpenedPopups");
