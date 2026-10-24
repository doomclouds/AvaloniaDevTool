using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Coordinates a bounded capture without blocking the UI or retaining application objects.</summary>
internal sealed class CompositionInspectionService(WeakObjectRegistry objects, Func<object, long> observe) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private int _capturing;
    private bool _disposed;

    public async Task<CompositionSnapshot> CaptureAsync(CompositionSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        Validate(request);
        if (objects.Get(request.ObjectId) is not Control root || OverlayService.IsDiagnosticVisual(root))
            throw new RpcException("unsupported_object", "Composition capture requires an application control.");
        if (!root.IsAttachedToVisualTree())
            throw new RpcException("detached_object", "Attach the control before capturing composition.");
        if (!double.IsFinite(root.Bounds.Width) || !double.IsFinite(root.Bounds.Height) ||
            root.Bounds.Width <= 0 || root.Bounds.Height <= 0)
            throw new RpcException("invalid_layout", "The capture root must have finite positive arranged bounds.");
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _capturing, 1, 0) != 0)
            throw new RpcException("capture_busy", "A composition capture is already running in this session.");

        using var detached = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token, detached.Token);
        void OnDetached(object? sender, VisualTreeAttachmentEventArgs args) => detached.Cancel();
        root.DetachedFromVisualTree += OnDetached;
        try
        {
            return await Avalonia12CompositionAdapter.CaptureAsync(root, request, observe, linked.Token);
        }
        catch (OperationCanceledException) when (detached.IsCancellationRequested)
        {
            throw new RpcException("detached_object", "The capture root was detached during composition capture.");
        }
        finally
        {
            root.DetachedFromVisualTree -= OnDetached;
            Interlocked.Exchange(ref _capturing, 0);
        }
    }

    private static void Validate(CompositionSnapshotRequest request)
    {
        if (request.MaxNodes is < 1 or > 256 || request.MaxDepth is < 0 or > 64 ||
            request.MaxImageBytes is < 1 or > 1048576 || request.MaxTotalImageBytes is < 1 or > 2097152 ||
            request.MaxTotalPixels is < 1 or > 4194304 ||
            !double.IsFinite(request.Scaling) || request.Scaling is < 0.1 or > 4)
            throw new RpcException("invalid_request", "Composition limits: nodes 1..256, depth 0..64, image bytes 1..1048576, total image bytes 1..2097152, pixels 1..4194304, scaling 0.1..4.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
