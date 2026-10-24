# Element overlays

The native Inspector can independently show bounds, margin, padding, element
information, horizontal and vertical rulers, and extended guidelines. The
default remains the original blue bounds highlight. Options persist when the
selection changes and can be changed while an element is already highlighted.
Picking and Escape use the same overlay service as explicit selection.

## Geometry and units

The agent captures all geometry on Avalonia's UI thread. Snapshot quads contain
four transformed corners in the selected presentation root's **DIPs**. They are
not replaced by an axis-aligned bounding box. Avalonia's `TransformToVisual`
includes nested layout offsets, mirroring, render transforms, and
`RenderTransformOrigin`. The information panel shows the actual arranged local
size, transformed local origin in root DIPs, and platform screen position in
device pixels. It also displays the actual root `RenderScaling`.

Bounds are blue, positive margin is orange, negative margin is pink, and padding
is green. Margin and padding are captured only through Avalonia properties;
arbitrary CLR getters are never invoked. A control with no registered Thickness
`Padding` property reports `padding_unavailable`. `Border` padding starts inside
its actual border thickness. Other registered Padding properties are shown as
an inset and report that a custom control/template may interpret them differently.
Effective strip widths follow Avalonia's layout rounding at the current scale;
the snapshot's Margin and Padding fields retain the declared values.

Margin strips visualize the signed local box offsets after the target's render
transform. Positive strips extend outside its arranged border box; negative
strips overlap the inside instead of being silently discarded. This does not
reconstruct the original parent arrange slot: that input rectangle is not public
framework state. Snapshot limitations state this distinction when margins are
enabled. Each padding side is clamped to the available inner border dimension;
opposing strips retain their overlap when their total exceeds that dimension,
and `padding_overlap` explains the resulting zero-sized content region.

Rulers show the target's horizontal and vertical root-space extents. Their
labels contain both DIP distances and device-pixel distances (`DIP ×
RenderScaling`). Root-edge ruler ticks and major ticks use the same scale;
`RulerTicks` returns both coordinates. Rotated or scaled local widths are not
mistaken for these root-space distances. Guidelines extend through the target's
transformed corners to the full root viewport. Rulers and guidelines are clipped
to that viewport, independently of the selected element's own content clip.

The box overlays respect ancestor bounds and geometry clips, including
transformed paths and Border corner radii. The selected element's own clip
applies to bounds/padding, while its external margin remains outside its own
content clip. Snapshot quads are the geometry *before* clipping; `IsClipped` and
`ClipOutlines` describe the declared clip regions. Clip outlines are bounding
quads for path clips rather than a serialized path representation. Opacity masks
and effect output are reported limitations. Non-finite or projective transforms
report `unsupported_transform` and draw no substitute identity geometry.

## Lifetime and isolation

One `HighlightAdorner` is reused when selections change. It is a root-sized plain
Canvas child in the root's existing AdornerLayer, positioned by the inverse
root/layer mapping. It does not adorn the TopLevel ancestor: Avalonia 12.1.4's
compositor explicitly refuses an adorned visual outside the layer manager's
descendant subtree. Drawing root geometry as a normal layer child avoids that
unsupported path and also avoids a strong target reference in AdornedElement.
For an overlay-hosted popup, the surface is placed directly into its existing
PopupOverlayLayer with a diagnostic child ZIndex above popup hosts. This avoids
the root AdornerLayer being covered by the popup while retaining full-root ruler
coordinates. The internal layer's identity is recognized through public visual
tree traversal; only public Canvas APIs are used to attach the surface.

The service holds the selected control weakly. Root layout updates refresh
positioning immediately, including deferred popup placement. A 33 ms UI timer
updates mutable transforms, ancestor changes, and render scaling. Explicit
snapshots update synchronously. Target detachment, root closure, clear, and
session disposal remove the surface and stop tracking. The drawing state retains
only DTOs and cloned clip geometries. Controls and their children are not hidden,
reparented, or assigned temporary application property values.

The entire diagnostic visual subtree is excluded from visual/logical/merged
tree traversal, search, layout snapshots, compositor snapshots, input targets,
and picking. The surface is neither hit-testable nor focusable. Screenshots
temporarily suspend the diagnostic surface using a nestable scope; application
visual visibility remains unchanged, and the overlay resumes after capture.
Native popup presentation roots receive their own surface. Overlay-hosted
popups use their actual owner presentation root.

## Protocol

- `overlay.configure`: `OverlayConfigureRequest(options, objectId?)`. Supplying
  an object selects it; omitting the ID updates the existing highlight.
- `overlay.snapshot`: `OverlaySnapshotRequest(objectId?)`. Supplying an ID
  inspects that object's geometry without changing the active highlight.
- Existing highlight and picking operations retain their behavior.

`DiagnosticsClient.ConfigureOverlayAsync` and `GetOverlaySnapshotAsync` provide
typed access. Snapshots contain actual quads, signed box strips, ruler distances,
major/minor tick coordinates, guideline segments, information text, scaling,
visibility, clipping, and explicit limitations. An inactive snapshot retains the
configured options. Expired, detached, and diagnostic targets fail explicitly.

## Control-specific information and box-model preview

`OverlayInfo.ControlInfo` adds bounded named fields for real `TextBlock`,
`TextBox`, and `Image` instances. Other controls keep the common information.
Each field is at most 120 characters (`truncated` status when needed), control characters are replaced, and the
field set is fixed by the supported control kind. Text contents are never
returned. Text information includes the effective public font properties,
wrapping/alignment and, for `TextBlock`, trimming, max lines and spacing. Actual
line count and layout width/height come from `TextBlock.TextLayout` or the real
`PART_TextPresenter.TextLayout` belonging to the selected `TextBox`. Only measured
exact framework layout types are read: a custom text layout or absent/unmeasured
presenter reports `unknown` with a status. A nonzero `TextBox.PasswordChar`
skips both text and presenter layout access, including text length; it reports
`redacted_password`, even if password reveal is active.

Image information identifies the actual source type and a weak registry object
ID. Exact framework `Bitmap`, `WriteableBitmap`, and `RenderTargetBitmap` sources
provide decoded `PixelSize`, source `Dpi`, and source size in DIPs. Source DPI is
separate from root rendering scale: a 90 x 60 bitmap at 144 x 120 DPI is 60 x 48
DIPs, regardless of the root scale. Stretch and stretch direction come from the
selected Image's public Avalonia properties. An arbitrary `IImage.Size` getter
and source `ToString()` are never called. Other image sources report explicit
unknown metadata. Image/Bitmap public APIs do not preserve a general source URI;
`SourceUri` reports `not_exposed_by_image_api` rather than inventing a filename.

The native overlay panel includes a read-only Margin/Border/Padding/Content
diagram (`Inspector.BoxModel`) with current four-side declared values and an
effective local content rectangle. Its ring widths are schematic, and the labels
are DIPs. Missing Padding/BorderThickness properties display `?`. Geometry reads
only styled Thickness properties; custom DirectProperty getters are excluded.
Border and padding insets follow layout rounding and are clamped like the
framework `Rect.Deflate`. Custom/template interpretations remain explicit
limitations. This diagram does not write layout values.

Pointer movement over each diagram side temporarily sends optional
`OverlayOptions.EmphasizedRegion` (`Margin`, `Border`, `Padding`, or `Content`)
and `EmphasizedSide` (`Left`, `Top`, `Right`, `Bottom`, or null for the entire
region). Existing callers omit these optional fields. During preview only the
matching application region is drawn; other bounds, regions, labels, rulers and
guidelines are suppressed. Border is yellow and content blue. The same affine
root geometry, render scale, ancestor/self clip handling and non-hit-testable
surface are used. A zero-width or unavailable region draws nothing.

Leaving restores the configured options and current selection, including a
previously hidden overlay. A serialized configuration loop and selection/hover
revisions prevent a stale exit or response from replacing a newer region or
selection. Clearing selection clears the temporary overlay. Disconnect cancels
the generation and clears diagram state. `BoxModelSnapshot` is refreshed on
selection and explicit geometry refresh; this Inspector diagram is not a
continuous property monitor. The target overlay itself retains its existing
33 ms geometry refresh.

## Evidence and validation

The implementation follows the public source for the pinned Avalonia 12.1.4
package:

- [VisualExtensions transform composition](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/VisualExtensions.cs)
- [Layoutable margin and arrange rounding](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Layout/Layoutable.cs)
- [LayoutHelper padding and border arrangement](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Layout/LayoutHelper.cs)
- [Border arrangement and clip radius](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Controls/Border.cs)
- [AdornerLayer layout and tracking](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Controls/Primitives/AdornerLayer.cs)
- [Compositor adorned-visual subtree restriction](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositionVisual/ServerCompositionVisual.Adorners.cs)
- [TextBlock public text layout](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Controls/TextBlock.cs)
- [TextBox actual template presenter and password handling](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Controls/TextBox.cs)
- [TextPresenter actual text layout](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Controls/Presenters/TextPresenter.cs)
- [Image source and stretch](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Controls/Image.cs)
- [Bitmap decoded dimensions and DPI](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Media/Imaging/Bitmap.cs)

The open-source Classic Diagnostics implementation was also inspected through
its [control highlight adorner](https://github.com/Sylinko/ClassicDiagnostics.Avalonia/blob/main/src/Views/Controls/ControlHighlightAdorner.cs)
and [element layout view](https://github.com/Sylinko/ClassicDiagnostics.Avalonia/blob/main/src/Views/Elements/Styles/ElementLayoutView.axaml.cs).
It demonstrates styled box values and layout-region interaction; this tool uses
its own root-space geometry and selected-control preview protocol. No additional
commercial binary inspection was needed for these features.

`OverlayTests` render the actual source overlay through Avalonia Headless with
Skia and inspect real pixels. They cover distinct bounds/margin/padding colors,
nested translation/rotation/scaling and center origin, clips, negative margins,
overlapping padding, DPI changes, option updates without reselection, root and
cross-window selection, owner-window popup overlay layers, clear/close cleanup, collection after clear,
and unsupported property/transform behavior. Typed-client tests also compare
root screenshots before and during active overlays and check search/layout
exclusion.

Additional tests compare real TextBlock/TextBox layout metrics, password
redaction, decoded bitmap pixels/DPI/weak identity, and custom source/layout
getters that must never execute. Region-preview pixel tests use a rotated target
at render scale 1.5, assert that other sides stay untouched, and restore the
original options. Inspector tests drive actual pointer movement across all four
diagram layers and different sides, click a real application button through the
overlay, reject stale exits, rapidly change the preview, reselect/clear, restore
a hidden overlay and disconnect while previewing.

Native popup discovery is tested separately by `PopupRootDiscoveryTests` with
`OverlayPopups=false`: real Flyout and nested PopupRoot discovery, focus and
highlight, configured-root isolation, and close cleanup. Discovery follows the
public `TopLevel.OpenedPopups` / `Popup.OpenedPopups` collections; Flyout's
internal Popup is absent from the logical tree. The Windows desktop follow-up
also observed the information overlay switch from `TextBox #PopupTextInput` to
the Flyout `CheckBox` at render scaling 2, and return to its button on Escape.
This does not establish ToolTip or other-platform desktop behavior.
