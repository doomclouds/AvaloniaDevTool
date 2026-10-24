using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

/// <summary>Verified common read operations; optional advanced features remain backend-specific.</summary>
public interface IInspectionBackend : IAsyncDisposable
{
    string BackendId { get; }
    string DisplayName { get; }
    bool IsConnected { get; }
    bool HasStableObjectIds { get; }
    bool HasPushUpdates { get; }
    string? LastNotice { get; }
    SessionInfo? Session { get; }
    event Action? ConnectionChanged;
    Task ConnectAsync(string endpoint, CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task<IReadOnlyList<TreeNodeInfo>> GetRootsAsync(TreeKind kind, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TreeNodeInfo>> GetChildrenAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PropertyInfoDto>> GetPropertiesAsync(long objectId, CancellationToken cancellationToken = default);
    Task<StyleSnapshot> GetStylesAsync(long objectId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TreeNodeInfo>> GetAncestorsAsync(long objectId, TreeKind kind, CancellationToken cancellationToken = default);
    Task<ElementScreenshot> CaptureElementAsync(long objectId, CancellationToken cancellationToken = default);
}

