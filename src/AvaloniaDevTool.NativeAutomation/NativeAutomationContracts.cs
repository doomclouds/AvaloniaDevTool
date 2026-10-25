namespace AvaloniaDevTool.NativeAutomation;

public sealed record NativeTargetIdentity(int ProcessId, DateTimeOffset StartTimeUtc);

public sealed record NativeAutomationOptions
{
    public string? WorkerPath { get; init; }
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed record NativeBounds(double X, double Y, double Width, double Height);

public sealed record NativeElementInfo
{
    public required string NodeId { get; init; }
    public string? ParentNodeId { get; init; }
    public int Depth { get; init; }
    public int ProcessId { get; init; }
    public long NativeWindowHandle { get; init; }
    public string AutomationId { get; init; } = "";
    public string Name { get; init; } = "";
    public string ControlType { get; init; } = "";
    public string ClassName { get; init; } = "";
    public string FrameworkId { get; init; } = "";
    public bool IsEnabled { get; init; }
    public bool IsOffscreen { get; init; }
    public bool IsPassword { get; init; }
    public bool HasKeyboardFocus { get; init; }
    public NativeBounds? Bounds { get; init; }
    public string? Value { get; init; }
    public string? ToggleState { get; init; }
    public string? ExpandCollapseState { get; init; }
    public bool? IsSelected { get; init; }
    public string? WindowVisualState { get; init; }
    public string[] SupportedPatterns { get; init; } = [];
}

public sealed record NativeWindowInfo
{
    public required string NodeId { get; init; }
    public long Handle { get; init; }
    public long OwnerHandle { get; init; }
    public int ProcessId { get; init; }
    public string Title { get; init; } = "";
    public string ClassName { get; init; } = "";
    public bool IsModal { get; init; }
    public bool IsOwned { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsVisible { get; init; }
    public string? WindowVisualState { get; init; }
}

public sealed record NativeTreeQuery
{
    public string? RootNodeId { get; init; }
    public int MaxDepth { get; init; } = 6;
    public int MaxNodes { get; init; } = 500;
}

public sealed record NativeTreeResult(NativeElementInfo[] Nodes, bool Truncated, int VisitedNodes);

public sealed record NativeFindQuery
{
    public string? RootNodeId { get; init; }
    public string? AutomationId { get; init; }
    public string? Name { get; init; }
    public string? ClassName { get; init; }
    public string? ControlType { get; init; }
    public int MaxDepth { get; init; } = 15;
    public int MaxNodes { get; init; } = 2000;
    public int MaxResults { get; init; } = 50;
}

public sealed record NativeFindResult(NativeElementInfo[] Matches, bool Truncated, int VisitedNodes);

public sealed record NativeActionRequest
{
    public required string NodeId { get; init; }
    // Invoke, SetValue, Select, AddToSelection, RemoveFromSelection, Expand, Collapse,
    // Toggle, Focus, ScrollIntoView, Scroll, Close, Minimize, Maximize, Restore.
    public required string Action { get; init; }
    public string? Value { get; init; }
    public string HorizontalScroll { get; init; } = "NoAmount";
    public string VerticalScroll { get; init; } = "NoAmount";
}

// Pending and dispatched are acknowledgements, not application completion claims.
// Native HWND button messages are dispatched; synchronous UIA providers may remain
// pending until their owned modal closes. Inspect UI state to verify either case.
public sealed record NativeActionResult(string NodeId, string Action, string Status, string? OperationId = null,
    string? ErrorCode = null, string? ErrorMessage = null);

public sealed record NativeInputRequest
{
    public required string NodeId { get; init; }
    public required string Kind { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public string? Text { get; init; }
    public string[] Keys { get; init; } = [];
    public string[] Modifiers { get; init; } = [];
    public string? TargetNodeId { get; init; }
    public double? TargetX { get; init; }
    public double? TargetY { get; init; }
    public int WheelDelta { get; init; } = 120;
    public int DragSteps { get; init; } = 12;
}

public sealed record NativeInputResult(string NodeId, string Kind, bool Injected, int InputCount,
    bool ApplicationEffectVerified = false, string? Note = null);

public sealed record NativeScreenshotRequest
{
    public required string NodeId { get; init; }
    // Optional node-local physical-pixel rectangle; specify all four values together.
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public int MaxPixels { get; init; } = 8_000_000;
}

public sealed record NativeScreenshotResult(string NodeId, string PngBase64, int Width, int Height,
    NativeBounds ScreenBounds, string CaptureMethod, string[] Limitations);

public sealed class NativeAutomationException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
