using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Protocol;

public static class InputMethods
{
    public const string Send = "input.send";
    public const string Click = "input.click";
    public const string Chord = "input.chord";
    public const string Drag = "input.drag";
    public const string TextSelection = "input.textSelection";
    public const string SelectItem = "input.selectItem";
    public const string ControlAction = "input.controlAction";
}

[JsonConverter(typeof(JsonStringEnumConverter<InputKind>))]
public enum InputKind
{
    Text,
    KeyDown,
    KeyUp,
    PointerMove,
    PointerDown,
    PointerUp,
    Wheel,
    Click
}

public sealed record InputRequest(
    long ObjectId,
    InputKind Kind,
    string? Text = null,
    string? Key = null,
    IReadOnlyList<string>? Modifiers = null,
    double? X = null,
    double? Y = null,
    double DeltaX = 0,
    double DeltaY = 0,
    string Button = "Left");

public sealed record InputResult(bool Delivered, bool Handled, string Detail);

public sealed record ClickInputRequest(
    long ObjectId,
    int ClickCount = 1,
    string Button = "Left",
    double? X = null,
    double? Y = null,
    IReadOnlyList<string>? Modifiers = null);

public sealed record KeyChordRequest(
    long ObjectId,
    IReadOnlyList<string> Keys,
    IReadOnlyList<string>? Modifiers = null);

/// <summary>A point in the named control's local coordinates, mapped to the drag's top-level.</summary>
public sealed record DragWaypoint(long ObjectId, double? X = null, double? Y = null);

/// <summary>Delivers a captured pointer gesture; it does not create an OS drag-and-drop data transfer.</summary>
public sealed record DragInputRequest(
    long SourceObjectId,
    long TargetObjectId,
    double? SourceX = null,
    double? SourceY = null,
    double? TargetX = null,
    double? TargetY = null,
    int Steps = 12,
    IReadOnlyList<DragWaypoint>? Path = null,
    string Button = "Left",
    IReadOnlyList<string>? Modifiers = null);

/// <summary>Uses UTF-16 indices. Null Length selects through the end; non-null ReplacementText edits the selection.</summary>
public sealed record TextSelectionRequest(
    long ObjectId,
    int Start = 0,
    int? Length = null,
    string? ReplacementText = null);

/// <summary>Reports actual selection state. Replaced is true only when replacement changed the text.</summary>
public sealed record TextSelectionResult(
    int SelectionStart,
    int SelectionEnd,
    int TextLength,
    string SelectedText,
    bool SelectedTextTruncated,
    bool Replaced);

public sealed record SelectItemRequest(long ObjectId, int Index);

public sealed record ItemSelectionResult(bool Succeeded, int SelectedIndex, string? SelectedItem, int ItemCount);

[JsonConverter(typeof(JsonStringEnumConverter<ControlAction>))]
public enum ControlAction
{
    Invoke,
    Toggle,
    Expand,
    Collapse
}

public sealed record ControlActionRequest(long ObjectId, ControlAction Action);

public sealed record ControlActionResult(
    bool Succeeded,
    ControlAction Action,
    string? ToggleState = null,
    string? ExpandCollapseState = null,
    string? Detail = null);
