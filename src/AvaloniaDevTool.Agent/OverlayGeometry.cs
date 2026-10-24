using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>UI-thread capture. The resulting drawing state contains no application controls.</summary>
internal sealed record OverlayGeometry(OverlaySnapshot Snapshot, IReadOnlyList<Geometry> AncestorClips,
    IReadOnlyList<Geometry> SelfClips)
{
    public static OverlayGeometry Capture(Control target, TopLevel root, long objectId, long rootId, OverlayOptions options,
        Func<object, long>? observe = null)
    {
        var limitations = new List<string>();
        var scale = root.RenderScaling;
        var viewport = new OverlayRect(0, 0, root.Bounds.Width, root.Bounds.Height);
        var margin = target.Margin;
        var padding = ReadThickness(target, "Padding");
        var border = ReadThickness(target, "BorderThickness");
        var effectiveMargin = target.UseLayoutRounding ? LayoutHelper.RoundLayoutThickness(margin, scale) : margin;
        var effectivePadding = target.UseLayoutRounding && padding is { } value
            ? LayoutHelper.RoundLayoutThickness(value, scale) : padding;
        var regions = new List<OverlayBoxRegion>();
        var rulers = new List<OverlayLine>();
        var guidelines = new List<OverlayLine>();
        var ticks = new List<OverlayRulerTick>();
        var outlines = new List<OverlayQuad>();
        var ancestors = new List<Geometry>();
        var ownClips = new List<Geometry>();
        OverlayQuad? bounds = null;
        OverlayQuad? content = null;
        OverlayRect? contentLocalRect = null;
        OverlayInfo? info = null;
        var visible = target.IsEffectivelyVisible;
        var clipped = false;

        if (padding is null && options.ShowPadding)
            limitations.Add("padding_unavailable: The control has no registered Avalonia styled Thickness Padding property.");
        if (target is not Border and not Decorator && padding is not null)
            limitations.Add("padding_semantics: Registered Padding is shown as an inset; custom/template layout may interpret it differently.");
        if (target.OpacityMask is not null || target.GetVisualAncestors().Any(visual => visual.OpacityMask is not null))
            limitations.Add("opacity_mask: Overlay geometry does not reproduce application opacity masks.");
        if (new[] { target }.Concat(target.GetVisualAncestors()).Any(visual => visual.Effect is not null))
            limitations.Add("effect: Overlay geometry does not include effect output outside the arranged box.");

        var transform = target.TransformToVisual(root);
        if (transform is not { } matrix || !FiniteAffine(matrix) ||
            target.GetSelfAndVisualAncestors().Any(visual => visual.RenderTransform is { } render && !FiniteAffine(render.Value)))
        {
            limitations.Add("unsupported_transform: The target cannot be represented by a finite 2D affine transform; no substitute geometry is drawn.");
            visible = false;
        }
        else if (!FiniteSize(target.Bounds.Size) || !FiniteSize(root.Bounds.Size))
        {
            limitations.Add("invalid_layout: Arranged bounds must be finite and non-negative.");
            visible = false;
        }
        else
        {
            var local = new Rect(target.Bounds.Size);
            bounds = Quad(local, matrix);
            BuildMargin(local, effectiveMargin, matrix, regions);
            if (effectivePadding is { } inset)
            {
                var effectiveBorder = target.UseLayoutRounding ? LayoutHelper.RoundLayoutThickness(border ?? default, scale) : border ?? default;
                BuildPadding(local.Deflate(effectiveBorder), inset, matrix, regions);
                if (inset.Left + inset.Right > local.Deflate(effectiveBorder).Width ||
                    inset.Top + inset.Bottom > local.Deflate(effectiveBorder).Height)
                    limitations.Add("padding_overlap: Opposing padding strips overlap; the content rectangle is clamped to zero as Rect.Deflate does.");
            }
            var borderInset = target.UseLayoutRounding ? LayoutHelper.RoundLayoutThickness(border ?? default, scale) : border ?? default;
            BuildPadding(local, borderInset, matrix, regions, "Border");
            var contentRect = local.Deflate(borderInset).Deflate(effectivePadding ?? default);
            content = Quad(contentRect, matrix);
            contentLocalRect = new(contentRect.X, contentRect.Y, contentRect.Width, contentRect.Height);
            if (border is not null && target is not Border)
                limitations.Add("border_semantics: Registered BorderThickness is an inset; a control template may draw it differently.");
            if (margin.Left < 0 || margin.Top < 0 || margin.Right < 0 || margin.Bottom < 0)
                limitations.Add("negative_margin: Signed negative margin is shown as an overlapping strip inside the arranged border box.");
            if (options.ShowMargin)
                limitations.Add("margin_box: Margin strips visualize signed local box offsets after the render transform; the original parent arrange slot is not public API.");

            foreach (var visual in target.GetSelfAndVisualAncestors())
            {
                if (visual.TransformToVisual(root) is not { } clipMatrix || !FiniteAffine(clipMatrix))
                {
                    limitations.Add("unsupported_clip_transform: An ancestor clip cannot be transformed to the root.");
                    visible = false;
                    break;
                }
                var collection = ReferenceEquals(visual, target) ? ownClips : ancestors;
                if (visual.ClipToBounds)
                {
                    // Border's actual rounded clipping is not equivalent to its axis-aligned bounds.
                    Geometry clip = visual is Border rounded && rounded.CornerRadius != default
                        ? RoundedClip(new Rect(visual.Bounds.Size), rounded.CornerRadius)
                        : new RectangleGeometry(new Rect(visual.Bounds.Size));
                    clip.Transform = new MatrixTransform(clipMatrix);
                    collection.Add(clip);
                    outlines.Add(Quad(new Rect(visual.Bounds.Size), clipMatrix));
                    clipped = true;
                }
                if (visual.Clip is { } geometry)
                {
                    var copy = geometry.Clone();
                    var geometryMatrix = geometry.Transform?.Value ?? Matrix.Identity;
                    if (!FiniteAffine(geometryMatrix))
                    {
                        limitations.Add("unsupported_clip_transform: A clip geometry has a non-affine transform.");
                        visible = false;
                        break;
                    }
                    copy.Transform = new MatrixTransform(geometryMatrix * clipMatrix);
                    collection.Add(copy);
                    outlines.Add(Quad(geometry.Bounds, clipMatrix));
                    clipped = true;
                }
                if (ReferenceEquals(visual, root)) break;
            }

            var points = Points(bounds).ToArray();
            var minX = points.Min(point => point.X);
            var maxX = points.Max(point => point.X);
            var minY = points.Min(point => point.Y);
            var maxY = points.Max(point => point.Y);
            clipped |= minX < 0 || minY < 0 || maxX > viewport.Width || maxY > viewport.Height;
            if (options.ShowRulers)
            {
                var step = Math.Max(10, Math.Ceiling(Math.Max(viewport.Width, viewport.Height) / 2000) * 10);
                AddTicks("Horizontal", viewport.Width);
                AddTicks("Vertical", viewport.Height);
                AddLine(rulers, "Horizontal", new Point(minX, 14), new Point(maxX, 14), scale,
                    DistanceLabel(maxX - minX, scale));
                AddLine(rulers, "Vertical", new Point(14, minY), new Point(14, maxY), scale,
                    DistanceLabel(maxY - minY, scale));
                void AddTicks(string orientation, double length)
                {
                    for (var i = 0; i <= length / step; i++)
                        ticks.Add(new OverlayRulerTick(orientation, i * step, i * step * scale, i % 5 == 0));
                }
            }
            if (options.ShowGuidelines)
            {
                foreach (var point in points.Distinct())
                {
                    AddLine(guidelines, "Horizontal", new Point(0, point.Y), new Point(viewport.Width, point.Y), scale);
                    AddLine(guidelines, "Vertical", new Point(point.X, 0), new Point(point.X, viewport.Height), scale);
                }
            }
            var origin = new Point().Transform(matrix);
            OverlayPoint? screen = null;
            try
            {
                var pixel = target.PointToScreen(default);
                screen = new OverlayPoint(pixel.X, pixel.Y);
            }
            catch (InvalidOperationException) { limitations.Add("screen_position_unavailable: The platform has no screen coordinate mapping."); }
            var typeName = OverlayControlInformation.Bound(target.GetType().FullName ?? target.GetType().Name);
            var name = target.Name is { } fullName ? OverlayControlInformation.Bound(fullName) : null;
            if (target.Name is { Length: > 120 } || (target.GetType().FullName?.Length ?? 0) > 120)
                limitations.Add("information_truncated: Type/name text is limited to 120 characters.");
            var text = string.Create(CultureInfo.InvariantCulture,
                $"{OverlayControlInformation.Bound(target.GetType().Name)}{(name is null ? "" : " #" + name)}\n{target.Bounds.Width:0.##} × {target.Bounds.Height:0.##} DIP\nroot ({origin.X:0.##}, {origin.Y:0.##}) DIP · scale {scale:0.###}");
            if (screen is not null)
                text += string.Create(CultureInfo.InvariantCulture, $"\nscreen ({screen.X:0.##}, {screen.Y:0.##}) px");
            var controlInfo = OverlayControlInformation.Capture(target, observe);
            text += OverlayControlInformation.Summary(controlInfo);
            info = new OverlayInfo(typeName, name, target.Bounds.Width, target.Bounds.Height, Point(origin), screen, text, controlInfo);
        }

        return new OverlayGeometry(new OverlaySnapshot(true, objectId, rootId, options, scale, viewport, bounds,
            Thickness(margin), padding is { } actual ? Thickness(actual) : null, regions, rulers, guidelines,
            info, visible, clipped, limitations, ClipOutlines: outlines, RulerTicks: ticks,
            Border: border is { } actualBorder ? Thickness(actualBorder) : null, Content: content,
            ContentLocalRect: contentLocalRect), ancestors, ownClips);
    }

    public static OverlaySnapshot Empty(OverlayOptions options) => new(false, null, null, options, 1,
        new OverlayRect(0, 0, 0, 0), null, new OverlayThickness(0, 0, 0, 0), null, [], [], [], null, false, false, []);

    private static void BuildMargin(Rect bounds, Thickness value, Matrix transform, List<OverlayBoxRegion> regions)
    {
        // Strip rectangles are normalized individually so a negative side remains a visible signed overlap.
        Add("Left", new Rect(Math.Min(-value.Left, 0), 0, Math.Abs(value.Left), bounds.Height), value.Left);
        var outsideLeft = Math.Max(value.Left, 0);
        var outsideRight = Math.Max(value.Right, 0);
        Add("Top", new Rect(value.Top > 0 ? -outsideLeft : 0, Math.Min(-value.Top, 0),
            bounds.Width + (value.Top > 0 ? outsideLeft + outsideRight : 0), Math.Abs(value.Top)), value.Top);
        Add("Right", new Rect(bounds.Width + Math.Min(value.Right, 0), 0, Math.Abs(value.Right), bounds.Height), value.Right);
        Add("Bottom", new Rect(value.Bottom > 0 ? -outsideLeft : 0, bounds.Height + Math.Min(value.Bottom, 0),
            bounds.Width + (value.Bottom > 0 ? outsideLeft + outsideRight : 0), Math.Abs(value.Bottom)), value.Bottom);
        void Add(string side, Rect rect, double thickness)
        {
            if (thickness != 0) regions.Add(new OverlayBoxRegion("Margin", side, thickness, thickness < 0, Quad(rect, transform)));
        }
    }

    private static void BuildPadding(Rect bounds, Thickness value, Matrix transform, List<OverlayBoxRegion> regions,
        string kind = "Padding")
    {
        // Each side is clamped independently, retaining overlap when opposite sides exhaust the content box.
        Add("Left", new Rect(bounds.X, bounds.Y, Math.Min(Math.Max(value.Left, 0), bounds.Width), bounds.Height), value.Left);
        Add("Top", new Rect(bounds.X, bounds.Y, bounds.Width, Math.Min(Math.Max(value.Top, 0), bounds.Height)), value.Top);
        var right = Math.Min(Math.Max(value.Right, 0), bounds.Width);
        var bottom = Math.Min(Math.Max(value.Bottom, 0), bounds.Height);
        Add("Right", new Rect(bounds.Right - right, bounds.Y, right, bounds.Height), value.Right);
        Add("Bottom", new Rect(bounds.X, bounds.Bottom - bottom, bounds.Width, bottom), value.Bottom);
        void Add(string side, Rect rect, double thickness)
        {
            if (thickness != 0) regions.Add(new OverlayBoxRegion(kind, side, thickness,
                value.Left + value.Right > bounds.Width || value.Top + value.Bottom > bounds.Height, Quad(rect, transform)));
        }
    }

    private static Thickness? ReadThickness(Control target, string name)
    {
        // DirectProperty getters can execute application code; only styled framework values are inspected.
        var property = AvaloniaPropertyRegistry.Instance.GetRegistered(target.GetType())
            .OfType<StyledProperty<Thickness>>().FirstOrDefault(property => property.Name == name);
        return property is null ? null : target.GetValue(property);
    }

    private static bool FiniteSize(Size size) => double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width >= 0 && size.Height >= 0;
    internal static bool FiniteAffine(Matrix matrix) => double.IsFinite(matrix.M11) && double.IsFinite(matrix.M12) &&
        double.IsFinite(matrix.M21) && double.IsFinite(matrix.M22) && double.IsFinite(matrix.M31) && double.IsFinite(matrix.M32) &&
        matrix.M13 == 0 && matrix.M23 == 0 && matrix.M33 == 1;
    internal static OverlayQuad Quad(Rect rect, Matrix matrix) => new(Point(rect.TopLeft.Transform(matrix)),
        Point(rect.TopRight.Transform(matrix)), Point(rect.BottomRight.Transform(matrix)), Point(rect.BottomLeft.Transform(matrix)));
    internal static IEnumerable<OverlayPoint> Points(OverlayQuad quad) => [quad.TopLeft, quad.TopRight, quad.BottomRight, quad.BottomLeft];
    private static OverlayPoint Point(Point point) => new(point.X, point.Y);
    private static OverlayThickness Thickness(Thickness value) => new(value.Left, value.Top, value.Right, value.Bottom);
    private static string DistanceLabel(double dip, double scale) =>
        string.Create(CultureInfo.InvariantCulture, $"{dip:0.##} DIP / {dip * scale:0.##} px");
    private static void AddLine(List<OverlayLine> lines, string kind, Point start, Point end, double scale, string? label = null)
    {
        var length = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
        lines.Add(new OverlayLine(kind, Point(start), Point(end), length, length * scale, label));
    }

    private static StreamGeometry RoundedClip(Rect rect, CornerRadius radius)
    {
        var factor = 1d;
        var horizontal = Math.Max(radius.TopLeft + radius.TopRight, radius.BottomLeft + radius.BottomRight);
        var vertical = Math.Max(radius.TopLeft + radius.BottomLeft, radius.TopRight + radius.BottomRight);
        if (horizontal > rect.Width) factor = Math.Min(factor, rect.Width / horizontal);
        if (vertical > rect.Height) factor = Math.Min(factor, rect.Height / vertical);
        var tl = radius.TopLeft * factor;
        var tr = radius.TopRight * factor;
        var br = radius.BottomRight * factor;
        var bl = radius.BottomLeft * factor;
        var geometry = new StreamGeometry();
        using var path = geometry.Open();
        path.BeginFigure(new Point(rect.Left + tl, rect.Top), true);
        path.LineTo(new Point(rect.Right - tr, rect.Top));
        Arc(new Point(rect.Right, rect.Top + tr), tr);
        path.LineTo(new Point(rect.Right, rect.Bottom - br));
        Arc(new Point(rect.Right - br, rect.Bottom), br);
        path.LineTo(new Point(rect.Left + bl, rect.Bottom));
        Arc(new Point(rect.Left, rect.Bottom - bl), bl);
        path.LineTo(new Point(rect.Left, rect.Top + tl));
        Arc(new Point(rect.Left + tl, rect.Top), tl);
        path.EndFigure(true);
        return geometry;
        void Arc(Point end, double value)
        {
            if (value <= 0) path.LineTo(end);
            else path.ArcTo(end, new Size(value, value), 0, false, SweepDirection.Clockwise);
        }
    }
}
