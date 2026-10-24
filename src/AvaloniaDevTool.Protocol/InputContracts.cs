using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Protocol;

public static class InputMethods
{
    public const string Send = "input.send";
}

[JsonConverter(typeof(JsonStringEnumConverter<InputKind>))]
public enum InputKind { Text, KeyDown, KeyUp, PointerMove, PointerDown, PointerUp, Wheel, Click }

public sealed record InputRequest(long ObjectId, InputKind Kind, string? Text = null, string? Key = null,
    IReadOnlyList<string>? Modifiers = null, double? X = null, double? Y = null,
    double DeltaX = 0, double DeltaY = 0, string Button = "Left");

public sealed record InputResult(bool Delivered, bool Handled, string Detail);

