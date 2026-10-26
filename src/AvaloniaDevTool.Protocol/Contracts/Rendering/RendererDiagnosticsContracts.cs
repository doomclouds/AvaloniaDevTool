using System.Text.Json.Serialization;

namespace AvaloniaDevTool.Protocol;

public static class RendererDiagnosticsMethods
{
    public const string Status = "renderer-diagnostics.status";
    public const string Set = "renderer-diagnostics.set";
    public const string Stop = "renderer-diagnostics.stop";
}

[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<RendererOverlayFlags>))]
public enum RendererOverlayFlags
{
    None = 0,
    Fps = 1,
    DirtyRects = 2,
    LayoutTimeGraph = 4,
    RenderTimeGraph = 8
}

/// <summary>A null window ID targets all current roots. Future roots are opt-in.</summary>
public sealed record RendererDiagnosticsSetRequest(
    long? WindowId,
    RendererOverlayFlags Flags,
    bool FollowNewWindows = false);

public sealed record RendererWindowDiagnosticsDto(
    long WindowId,
    string Title,
    RendererOverlayFlags CurrentFlags,
    RendererOverlayFlags AddedFlags,
    bool HasOverride,
    bool HasConflict);

public sealed record RendererDiagnosticsStatusDto(
    bool IsActive,
    bool FollowNewWindows,
    RendererOverlayFlags SupportedFlags,
    IReadOnlyList<RendererWindowDiagnosticsDto> Windows,
    IReadOnlyList<string> Conflicts,
    string Source = "TopLevel.RendererDiagnostics.DebugOverlays",
    string TimingScope = "Framework renderer overlays; no synthetic FPS or timer measurements");

public sealed record AvaloniaMetricsSupportDto(
    bool IsInitialized,
    bool CachedDiagnosticSwitch,
    string FrameworkVersion,
    string Source,
    string? Limitation = null);
