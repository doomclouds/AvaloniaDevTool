using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Owns a reusable overlay surface and only a weak reference to the selected control.</summary>
internal sealed class OverlayService : IDisposable
{
    private readonly WeakObjectRegistry _objects;
    private readonly Func<object, long> _observe;
    private readonly DispatcherTimer _timer;
    private WeakReference<Control>? _target;
    private TopLevel? _root;
    private Canvas? _layer;
    private HighlightAdorner? _adorner;
    private OverlayOptions _options = new();
    private int _suspensions;
    private bool _disposed;

    public OverlayService(WeakObjectRegistry objects, Func<object, long> observe)
    {
        _objects = objects;
        _observe = observe;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
    }

    public Control? CurrentTarget => _target is { } reference && reference.TryGetTarget(out var target) ? target : null;
    public HighlightAdorner? Adorner => _adorner;
    public OverlayOptions Options => _options;

    public OverlaySnapshot Configure(OverlayConfigureRequest request)
    {
        VerifyAccess();
        if (request.Options is null)
            throw new RpcException("invalid_request", "Overlay options are required.");
        if (request.Options.EmphasizedRegion is not null and not "Margin" and not "Border" and not "Padding"
                and not "Content" ||
            request.Options.EmphasizedSide is not null and not "Left" and not "Top" and not "Right" and not "Bottom")
            throw new RpcException("invalid_request", "Unsupported box-model region or side.");
        var target = request.ObjectId is { } objectId ? GetTarget(objectId) : null;
        _options = request.Options;
        if (target is not null && !Show(target))
            throw new RpcException("unsupported_overlay",
                "The presentation root has no supported diagnostic overlay surface.");
        return Snapshot(new OverlaySnapshotRequest());
    }

    public OverlaySnapshot Snapshot(OverlaySnapshotRequest request)
    {
        VerifyAccess();
        if (request.ObjectId is { } id)
        {
            var target = GetTarget(id);
            var root = GetRoot(target);
            return OverlayGeometry.Capture(target, root, _observe(target), _observe(root), _options, _observe).Snapshot;
        }

        return Refresh()?.Snapshot ?? OverlayGeometry.Empty(_options);
    }

    public bool Show(Control target)
    {
        VerifyAccess();
        if (IsDiagnosticVisual(target))
            return false;
        var root = GetRoot(target);
        var layer = GetLayer(target, root);
        if (layer is null)
            return false;
        if (root.TransformToVisual(layer) is not { } transform || !OverlayGeometry.FiniteAffine(transform))
            throw new RpcException("unsupported_transform",
                "The overlay layer cannot be mapped to its root with a finite 2D affine transform.");
        if (CurrentTarget is { } previous)
            previous.DetachedFromVisualTree -= OnTargetDetached;
        if (!ReferenceEquals(_root, root) || !ReferenceEquals(_layer, layer))
        {
            DetachSurface();
            _root = root;
            _layer = layer;
            _root.Closed += OnRootClosed;
            _root.LayoutUpdated += OnLayoutUpdated;
            _adorner ??= new HighlightAdorner { ZIndex = int.MaxValue };
            layer.Children.Add(_adorner);
        }

        _target = new WeakReference<Control>(target);
        target.DetachedFromVisualTree += OnTargetDetached;
        _timer.Start();
        Refresh();
        return true;
    }

    public void Clear()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (CurrentTarget is { } target)
            target.DetachedFromVisualTree -= OnTargetDetached;
        _target = null;
        _timer.Stop();
        _adorner?.ClearGeometry();
        DetachSurface();
    }

    public IDisposable Suspend()
    {
        VerifyAccess();
        _suspensions++;
        if (_adorner is not null)
            _adorner.IsVisible = false;
        return new Suspension(this);
    }

    public static bool IsDiagnosticVisual(object? value) => value is Visual visual &&
                                                            visual.GetSelfAndVisualAncestors().Any(ancestor =>
                                                                ancestor is HighlightAdorner);

    internal static Canvas? GetLayer(Control target, TopLevel root)
    {
        // PopupOverlayLayer is internal in the pinned framework. We use only the public visual tree
        // and Canvas.Children API; no reflection is needed to access its existing instance.
        var popupLayer = target.GetVisualAncestors().FirstOrDefault(ancestor =>
            ancestor.GetType().FullName == "Avalonia.Controls.Primitives.PopupOverlayLayer") as Canvas;
        return popupLayer ?? AdornerLayer.GetAdornerLayer(root);
    }

    private Control GetTarget(long id)
    {
        if (_objects.Get(id) is not Control target || IsDiagnosticVisual(target))
            throw new RpcException("unsupported_object", "Overlays require an application control.");
        _ = GetRoot(target);
        return target;
    }

    private static TopLevel GetRoot(Control target) =>
        TopLevel.GetTopLevel(target) is { } root && target.IsAttachedToVisualTree()
            ? root
            : throw new RpcException("detached_object", "Attach the control to a top-level before showing an overlay.");

    private OverlayGeometry? Refresh()
    {
        if (CurrentTarget is not { } target || _root is null || !target.IsAttachedToVisualTree() ||
            !ReferenceEquals(TopLevel.GetTopLevel(target), _root))
        {
            Clear();
            return null;
        }

        var geometry = OverlayGeometry.Capture(target, _root, _observe(target), _observe(_root), _options, _observe);
        var surfaceTransform = _root.TransformToVisual(_layer!);
        if (surfaceTransform is not { } transform || !OverlayGeometry.FiniteAffine(transform))
        {
            geometry = geometry with
            {
                Snapshot = geometry.Snapshot with
                {
                    IsVisible = false,
                    Limitations =
                    [
                        .. geometry.Snapshot.Limitations,
                        "unsupported_overlay_transform: The overlay layer no longer has a finite affine mapping to its root."
                    ]
                }
            };
        }

        if (_adorner is not null)
        {
            // Avalonia's compositor only supports adorning descendants of the layer manager.
            // A root overlay is a plain Canvas child so a TopLevel ancestor is never fed to that path.
            _adorner.Width = _root.Bounds.Width;
            _adorner.Height = _root.Bounds.Height;
            _adorner.RenderTransformOrigin = RelativePoint.TopLeft;
            if (geometry.Snapshot.IsVisible && surfaceTransform is { } currentTransform &&
                _adorner.RenderTransform?.Value != currentTransform)
                _adorner.RenderTransform = new Avalonia.Media.MatrixTransform(currentTransform);
            _adorner.SetGeometry(geometry);
            _adorner.IsVisible = _suspensions == 0;
        }

        return geometry;
    }

    private void DetachSurface()
    {
        if (_root is not null)
        {
            _root.Closed -= OnRootClosed;
            _root.LayoutUpdated -= OnLayoutUpdated;
        }

        if (_adorner is not null)
        {
            _layer?.Children.Remove(_adorner);
            AdornerLayer.SetAdornedElement(_adorner, null);
        }

        _layer = null;
        _root = null;
    }

    private void OnTick(object? sender, EventArgs args) => Refresh();
    private void OnLayoutUpdated(object? sender, EventArgs args) => Refresh();
    private void OnRootClosed(object? sender, EventArgs args) => Clear();
    private void OnTargetDetached(object? sender, VisualTreeAttachmentEventArgs args) => Clear();

    private void VerifyAccess()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Clear();
        _timer.Tick -= OnTick;
        _disposed = true;
    }

    private sealed class Suspension(OverlayService owner) : IDisposable
    {
        private OverlayService? _owner = owner;

        public void Dispose()
        {
            if (_owner is not { } service)
                return;
            Dispatcher.UIThread.VerifyAccess();
            _owner = null;
            service._suspensions--;
            if (!service._disposed && service._adorner is { } adorner)
                adorner.IsVisible = service._suspensions == 0 && service.CurrentTarget is not null;
        }
    }
}
