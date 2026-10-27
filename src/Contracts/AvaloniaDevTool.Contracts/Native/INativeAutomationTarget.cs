namespace AvaloniaDevTool.NativeAutomation;

/// <summary>The target-scoped native operations needed by the file selection capability.</summary>
public interface INativeAutomationTarget
{
    Task<NativeWindowInfo[]> GetWindowsAsync(CancellationToken cancellationToken = default);
    Task<NativeTreeResult> GetTreeAsync(NativeTreeQuery? query = null, CancellationToken cancellationToken = default);
    Task<NativeElementInfo> GetElementAsync(string nodeId, CancellationToken cancellationToken = default);
    Task<NativeActionResult> ExecuteAsync(NativeActionRequest request, CancellationToken cancellationToken = default);
    Task<NativeActionResult> GetActionStatusAsync(string operationId, CancellationToken cancellationToken = default);
}
