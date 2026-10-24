# Renderer diagnostics and framework metrics

## Renderer overlays

The Inspector Exploration page exposes the four public Avalonia 12.1.4
`RendererDebugOverlays` values: `Fps`, `DirtyRects`, `LayoutTimeGraph`, and
`RenderTimeGraph`. The agent writes the actual
`TopLevel.RendererDiagnostics.DebugOverlays` property. FPS and graphs are drawn
by Avalonia's compositor; the Inspector does not calculate FPS from a timer.
`RenderTimeGraph` is the framework render timing graph. No additional render
server flags or CPU/GPU timing claims are exposed.

Choose an individual current presentation root, or all roots. All-roots mode can
opt into roots opened later, including popup presentation roots discovered by
the existing session root enumeration. The session refreshes root membership;
it restores tool-owned values before releasing a removed root.

Flags are additive to each window's captured application value. Changing the
tool selection replaces only the tool request. Stop and disconnect restore that
captured value if the application has not changed the property. An application
`DebugOverlays` change relinquishes the tool's ownership of the entire property,
preserves the later application value, and reports a bounded conflict history.
This also detects a different value written synchronously from an application
property-changed handler. A repeated assignment of an already equal value emits
no framework change event and cannot express a detectable ownership change.

RPC methods:

- `renderer-diagnostics.status`: current roots, actual flags, tool-added flags,
  ownership, conflicts, and supported flag mask.
- `renderer-diagnostics.set`: `windowId` (null means all), `flags`, and
  `followNewWindows` (only valid for all roots).
- `renderer-diagnostics.stop`: restore owned values and stop future-root tracking.

These operations belong to the native backend. Unsupported flag bits and stale
window IDs fail explicitly. Framework rendering/platform support determines the
visible overlay result; reading the property alone is not proof of rendered FPS.

The MCP tool equivalents are `renderer-diagnostics`,
`renderer-diagnostics-set`, and `renderer-diagnostics-stop`; they use the same
native client API and ownership rules.

## Actual Avalonia metrics

`MetricsService` uses `MeterListener` to subscribe to existing framework and
runtime instruments. It initializes the real `Avalonia.Diagnostic.Meter` through
a private adapter restricted to the verified Avalonia **12.1.4** version and
method/field shape. The adapter never creates a replacement meter, never renames
an application meter, and never writes the cached diagnostics switch.

The exact framework source distinguishes two mechanisms:

1. `Diagnostic.IsEnabled` is cached in the static constructor from the
   `Avalonia.Diagnostics.Diagnostic.IsEnabled` AppContext switch. When false,
   automatic initialization of activities and metrics is skipped.
2. The verified layout/compositor timing call sites invoke `Diagnostic.Begin*`
   unconditionally. Those helpers test whether their histogram exists, then
   record elapsed stopwatch time only while that histogram has a listener.
   Calling the framework's `InitMetrics` once after startup therefore enables
   these actual measurement paths while leaving the cached switch unchanged.

The adapter first reads the framework histogram field to finish static
initialization. Existing histograms are retained. `InitMetrics` is called only
when they are absent. The metrics status exposes `AvaloniaSupport` with the
framework version, real meter name, initialization result, cached switch value,
and an explicit limitation if the private shape/version is unsupported.

Real instruments include:

| Instrument | Source | Unit |
| --- | --- | --- |
| `avalonia.ui.measure.time` | `LayoutManager.ExecuteMeasurePass` | ms |
| `avalonia.ui.arrange.time` | `LayoutManager.ExecuteArrangePass` | ms |
| `avalonia.ui.render.time` | `CompositingRenderer.Update` | ms |
| `avalonia.comp.update.time` | `ServerCompositionTarget.Update` | ms |
| `avalonia.comp.render.time` | `ServerCompositionTarget.Render` | ms |

The framework also publishes observable counts for rooted visuals, registered
event handlers, and dispatcher timers. `System.Runtime` remains a separate
runtime meter and is captured using its actual instrument identities.

For other framework versions, or when early diagnostics are desired, opt in
before any Avalonia use:

```csharp
AppContext.SetSwitch("Avalonia.Diagnostics.Diagnostic.IsEnabled", true);
// Configure and start Avalonia afterwards.
```

The equivalent application project setting is:

```xml
<ItemGroup>
  <RuntimeHostConfigurationOption
      Include="Avalonia.Diagnostics.Diagnostic.IsEnabled" Value="true" />
</ItemGroup>
```

This process-wide bootstrap remains an application choice. An unknown version
does not receive an unverified private late-initialization call.

## Source evidence and verification

The inspected official source is tag `12.1.4`, commit
`2d79ba55fae1b1e71c69f93a668080c9029e6a31`. It is available locally under
`%TEMP%/avdt-avalonia-source-12.1.4` (a sparse checkout, so some files are read
with `git show HEAD:<path>`).

- [Public renderer flags](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Rendering/RendererDebugOverlays.cs)
- [Renderer diagnostics property](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Rendering/RendererDiagnostics.cs)
- [TopLevel renderer diagnostics](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Controls/TopLevel.cs)
- [Renderer-to-compositor forwarding and render recording timing](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Rendering/Composition/CompositingRenderer.cs)
- [Compositor update/render timing and native overlay drawing](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Rendering/Composition/Server/ServerCompositionTarget.cs)
- [Cached diagnostics switch](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Diagnostics/Diagnostic.cs)
- [Real meter initialization and histogram recording](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Diagnostics/Diagnostic.Metrics.cs)
- [Instrument names and units](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Diagnostics/Diagnostic.Consts.cs)
- [Actual layout timing call sites](https://github.com/AvaloniaUI/Avalonia/blob/12.1.4/src/Avalonia.Base/Layout/LayoutManager.cs)

`RendererDiagnosticsTests` verifies window scope, application flag preservation,
new and removed roots, application-change conflicts, and named-pipe disconnect
restoration. Its Skia headless test captures actual rendered window frames,
asserts that enabling all four native flags changes visible pixels, then asserts
that stopping restores the original frame. Its meter test generates real layout
and rendering work, asserts measured samples for all five framework timing
histograms and real `System.Runtime` instruments, and checks that the cached
diagnostic switch is unchanged. Inspector tests load the AXAML view and exercise
its real commands through a connected target.
