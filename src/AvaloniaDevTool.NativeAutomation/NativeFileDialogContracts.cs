namespace AvaloniaDevTool.NativeAutomation;

public sealed record NativeFileDialogOptions
{
    public int MaxDepth { get; init; } = 32;
    public int MaxNodes { get; init; } = 5000;
}

public enum NativeFileDialogKind
{
    Unknown,
    OpenFile,
    OpenFiles,
    SaveFile,
    Folder
}

public sealed record NativeFileDialogPrompt
{
    public required NativeWindowInfo Window { get; init; }
    public required string Message { get; init; }
    public string? YesNodeId { get; init; }
    public string? NoNodeId { get; init; }
    public bool ExpectedOverwrite { get; init; }
}

/// <summary>
/// Describes a real, owner-scoped Windows common item dialog. Closed describes
/// only the dialog lifecycle; the application must independently verify its I/O.
/// </summary>
public sealed record NativeFileDialogInfo
{
    public required string DialogId { get; init; }
    public required NativeWindowInfo Window { get; init; }
    public string ProviderProfile { get; init; } = "windows-common-item-dialog-v1";
    public NativeFileDialogKind RequestedKind { get; init; }
    // Open, ConfirmationRequired, AcceptedDialogClosed, CancelledDialogClosed,
    // Closed (an externally closed dialog), ActionPending, or ActionFailed.
    public string State { get; init; } = "Open";
    public string? PathInputNodeId { get; init; }
    public string? PathInputValue { get; init; }
    public string? AcceptNodeId { get; init; }
    public string? CancelNodeId { get; init; }
    public string? FilterNodeId { get; init; }
    public string? FilterValue { get; init; }
    public string[] FilterOptions { get; init; } = [];
    public string[] RequestedPaths { get; init; } = [];
    public NativeFileDialogPrompt[] Prompts { get; init; } = [];
    public NativeActionResult? LastAction { get; init; }
    public bool ApplicationIoVerified { get; init; }
    public string? Note { get; init; }
}
