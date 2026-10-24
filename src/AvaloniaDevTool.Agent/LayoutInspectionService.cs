using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Captures bounded visual geometry without rasterizing or invoking control rendering.</summary>
internal sealed class LayoutInspectionService(WeakObjectRegistry objects, Func<object, long> observe)
{
    public LayoutSnapshot Snapshot(LayoutSnapshotRequest request)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (request.MaxNodes is < 1 or > 256 || request.MaxDepth is < 0 or > 64)
            throw new RpcException("invalid_request", "MaxNodes must be 1..256 and MaxDepth must be 0..64.");

        if (objects.Get(request.ObjectId) is not Control root || OverlayService.IsDiagnosticVisual(root))
            throw new RpcException("unsupported_object", "Layout snapshots require an application control.");
        if (!root.IsAttachedToVisualTree())
            throw new RpcException("detached_object", "Attach the control to a visual tree before capturing layout.");
        if (!Finite(root.Bounds.Width) || !Finite(root.Bounds.Height) || root.Bounds.Width <= 0 || root.Bounds.Height <= 0)
            throw new RpcException("invalid_layout", "The root must have finite, positive arranged bounds.");

        var ancestorVisible = true;
        var ancestorClipped = false;
        var ancestorCount = 0;
        foreach (var ancestor in root.GetVisualAncestors())
        {
            if (++ancestorCount > 1024)
                throw new RpcException("layout_limit", "The visual ancestor chain exceeds the snapshot limit.");
            ancestorVisible &= ancestor.IsVisible;
            ancestorClipped |= HasClip(ancestor);
        }

        var layers = new List<LayoutLayer>(Math.Min(request.MaxNodes, 128));
        var truncated = false;
        var rootId = observe(root);
        Visit(root, null, 0, ancestorVisible, ancestorClipped);
        return new LayoutSnapshot(rootId, root.Bounds.Width, root.Bounds.Height,
            layers.ToArray(), layers.Count, truncated);

        void Visit(Visual visual, long? parentId, int depth, bool parentVisible, bool parentClipped)
        {
            var matrix = ReferenceEquals(visual, root) ? Matrix.Identity : visual.TransformToVisual(root);
            if (matrix is not { } transform || !FiniteAffine(transform))
                throw new RpcException("unsupported_transform", "The visual transform cannot be represented as a finite affine matrix.");
            var width = visual.Bounds.Width;
            var height = visual.Bounds.Height;
            if (!Finite(width) || !Finite(height) || width < 0 || height < 0)
                throw new RpcException("invalid_layout", "A visual has invalid arranged bounds.");

            var id = ReferenceEquals(visual, root) ? rootId : observe(visual);
            var visible = parentVisible && visual.IsVisible;
            var clipped = parentClipped || HasClip(visual);
            layers.Add(new LayoutLayer(id, parentId, depth, visual.GetType().FullName ?? visual.GetType().Name,
                (visual as StyledElement)?.Name, width, height, transform.M11, transform.M12,
                transform.M21, transform.M22, transform.M31, transform.M32, visible, clipped));

            foreach (var child in visual.GetVisualChildren())
            {
                if (OverlayService.IsDiagnosticVisual(child))
                    continue;
                // Observe the first omitted child without materializing the whole sibling collection.
                if (depth >= request.MaxDepth || layers.Count >= request.MaxNodes)
                {
                    truncated = true;
                    break;
                }
                Visit(child, id, depth + 1, visible, clipped);
            }
        }
    }

    private static bool HasClip(Visual visual) => visual.ClipToBounds || visual.Clip is not null;
    private static bool Finite(double value) => double.IsFinite(value);
    private static bool FiniteAffine(Matrix matrix) =>
        Finite(matrix.M11) && Finite(matrix.M12) && Finite(matrix.M21) && Finite(matrix.M22) &&
        Finite(matrix.M31) && Finite(matrix.M32) &&
        matrix.M13 == 0 && matrix.M23 == 0 && matrix.M33 == 1;
}

