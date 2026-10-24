using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Protocol;

public static class BrushMethods
{
    public const string Set = "properties.brush.set";
}

[JsonConverter(typeof(JsonStringEnumConverter<BrushKind>))]
public enum BrushKind { Solid, Linear, Radial, Conic }

public sealed record BrushStopDto(string Color, double Offset);

// Coordinates retain Avalonia's absolute/relative syntax (for example "50%, 50%").
public sealed record BrushDto(BrushKind Kind, double Opacity = 1, string Color = "#FFFFFFFF",
    IReadOnlyList<BrushStopDto>? Stops = null, string SpreadMethod = "Pad",
    string StartPoint = "0%, 0%", string EndPoint = "100%, 100%",
    string Center = "50%, 50%", string GradientOrigin = "50%, 50%",
    string RadiusX = "50%", string RadiusY = "50%", double Angle = 0,
    string? Transform = null, string TransformOrigin = "0%, 0%",
    IReadOnlyList<BrushKind>? AllowedKinds = null);

public sealed record SetBrushRequest(long ObjectId, string PropertyId, BrushDto Brush);
