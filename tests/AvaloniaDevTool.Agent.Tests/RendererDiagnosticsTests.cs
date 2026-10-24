using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering;
using Avalonia.Threading;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using SkiaSharp;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class RendererDiagnosticsTests
{
    [AvaloniaFact]
    public void Window_scope_preserves_application_flags_tracks_new_roots_and_restores_removed_roots()
    {
        var first = new Window { Title = "First" };
        var second = new Window { Title = "Second" };
        first.Show(); second.Show();
        try
        {
            first.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.RenderTimeGraph;
            var objects = new WeakObjectRegistry();
            using var service = new RendererDiagnosticsService(objects);
            service.RefreshRoots([first]);
            service.Set(new(objects.GetId(first), RendererOverlayFlags.Fps));
            Assert.Equal(RendererDebugOverlays.RenderTimeGraph | RendererDebugOverlays.Fps,
                first.RendererDiagnostics.DebugOverlays);
            Assert.Equal(RendererDebugOverlays.None, second.RendererDiagnostics.DebugOverlays);
            service.Set(new(null, RendererOverlayFlags.DirtyRects, FollowNewWindows: true));
            service.RefreshRoots([first, second]);
            Assert.Equal(RendererDebugOverlays.DirtyRects, second.RendererDiagnostics.DebugOverlays);
            Assert.Equal(RendererDebugOverlays.RenderTimeGraph | RendererDebugOverlays.DirtyRects,
                first.RendererDiagnostics.DebugOverlays);
            service.RefreshRoots([second]);
            Assert.Equal(RendererDebugOverlays.RenderTimeGraph, first.RendererDiagnostics.DebugOverlays);
            var stopped = service.Stop();
            Assert.False(stopped.IsActive);
            Assert.Equal(RendererDebugOverlays.None, second.RendererDiagnostics.DebugOverlays);
            Assert.Empty(stopped.Conflicts);
        }
        finally { first.Close(); second.Close(); }
    }

    [AvaloniaFact]
    public void Application_changes_release_ownership_and_are_not_overwritten_on_stop_or_dispose()
    {
        var window = new Window(); window.Show();
        try
        {
            var objects = new WeakObjectRegistry();
            using var service = new RendererDiagnosticsService(objects);
            service.RefreshRoots([window]);
            service.Set(new(objects.GetId(window), RendererOverlayFlags.Fps));
            window.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.LayoutTimeGraph;
            var stopped = service.Stop();
            Assert.Equal(RendererDebugOverlays.LayoutTimeGraph, window.RendererDiagnostics.DebugOverlays);
            Assert.Single(stopped.Conflicts);
            Assert.True(Assert.Single(stopped.Windows).HasConflict);
            Assert.False(Assert.Single(stopped.Windows).HasOverride);
            service.Dispose();
            Assert.Equal(RendererDebugOverlays.LayoutTimeGraph, window.RendererDiagnostics.DebugOverlays);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Synchronous_application_rewrite_is_detected_and_invalid_targets_do_not_mutate_flags()
    {
        var window = new Window(); window.Show();
        try
        {
            window.RendererDiagnostics.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RendererDiagnostics.DebugOverlays) &&
                    window.RendererDiagnostics.DebugOverlays == RendererDebugOverlays.Fps)
                    window.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.LayoutTimeGraph;
            };
            var objects = new WeakObjectRegistry();
            using var service = new RendererDiagnosticsService(objects);
            service.RefreshRoots([window]);
            var applied = service.Set(new(objects.GetId(window), RendererOverlayFlags.Fps));
            Assert.True(Assert.Single(applied.Windows).HasConflict);
            Assert.Single(applied.Conflicts);
            service.Stop();
            Assert.Equal(RendererDebugOverlays.LayoutTimeGraph, window.RendererDiagnostics.DebugOverlays);
            Assert.Equal("invalid_request", Assert.Throws<RpcException>(() =>
                service.Set(new(null, (RendererOverlayFlags)256))).Code);
            Assert.Equal("window_not_found", Assert.Throws<RpcException>(() =>
                service.Set(new(long.MaxValue, RendererOverlayFlags.Fps))).Code);
            Assert.Equal(RendererDebugOverlays.LayoutTimeGraph, window.RendererDiagnostics.DebugOverlays);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Native_renderer_overlay_changes_actual_frame_pixels_and_stop_restores_the_frame()
    {
        var window = new Window { Width = 640, Height = 360, Background = Brushes.White,
            Content = new Border { Background = Brushes.White } };
        window.Show();
        try
        {
            var objects = new WeakObjectRegistry();
            using var service = new RendererDiagnosticsService(objects);
            service.RefreshRoots([window]);
            window.UpdateLayout();
            using var before = Capture(window);
            service.Set(new(objects.GetId(window), RendererOverlayFlags.Fps | RendererOverlayFlags.LayoutTimeGraph |
                RendererOverlayFlags.RenderTimeGraph | RendererOverlayFlags.DirtyRects));
            using var overlay = Capture(window);
            Assert.True(ChangedPixels(before, overlay) > 200, "Native renderer diagnostics must produce visible frame pixels.");
            Assert.Contains(Enumerable.Range(0, overlay.Height).SelectMany(y => Enumerable.Range(0, overlay.Width)
                .Select(x => overlay.GetPixel(x, y))), color => color != SKColors.White);
            service.Stop();
            using var restored = Capture(window);
            Assert.Equal(0, ChangedPixels(before, restored));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Named_pipe_disconnect_restores_owned_flags()
    {
        var window = new Window { Title = "RPC overlays" }; window.Show();
        try
        {
            window.RendererDiagnostics.DebugOverlays = RendererDebugOverlays.RenderTimeGraph;
            await using var agent = DiagnosticsAgent.Attach(Application.Current!, new DiagnosticsAgentOptions
            {
                PipeName = "renderer-diagnostics-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window]
            });
            await using var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            var state = await client.GetRendererDiagnosticsAsync();
            var root = Assert.Single(state.Windows);
            var applied = await client.SetRendererDiagnosticsAsync(new(root.WindowId, RendererOverlayFlags.Fps));
            Assert.True(applied.IsActive);
            Assert.Equal(RendererOverlayFlags.Fps, Assert.Single(applied.Windows).AddedFlags);
            await client.DisconnectAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (window.RendererDiagnostics.DebugOverlays != RendererDebugOverlays.RenderTimeGraph)
                await Task.Delay(10, timeout.Token);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Framework_meter_reports_real_layout_render_and_runtime_measurements()
    {
        var diagnostic = typeof(AvaloniaObject).Assembly.GetType("Avalonia.Diagnostics.Diagnostic")!;
        var cachedSwitch = diagnostic.GetProperty("IsEnabled", System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public)!;
        var beforeSwitch = cachedSwitch.GetValue(null);
        using var service = new MetricsService((_, _) => { });
        var started = service.Start(new MetricsStartRequest(MeterNames: [Avalonia12MetricsAdapter.MeterName, "System.Runtime"],
            Capacity: 1024, MaxSeries: 128, PollIntervalMs: 100, NotificationIntervalMs: 5000));
        var support = Assert.IsType<AvaloniaMetricsSupportDto>(started.AvaloniaSupport);
        Assert.True(support.IsInitialized, support.Limitation);
        Assert.Equal(beforeSwitch, cachedSwitch.GetValue(null));
        Assert.Equal(beforeSwitch, support.CachedDiagnosticSwitch);
        var content = new TextBlock { Text = "Real framework metric activity" };
        var window = new Window { Width = 360, Height = 200, Content = content };
        window.Show();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                content.Text += " sample";
                content.InvalidateMeasure();
                window.UpdateLayout();
                using var frame = Capture(window);
            }
            await Task.Delay(160);
            var snapshot = service.Snapshot();
            var framework = snapshot.Instruments.Where(instrument => instrument.MeterName == Avalonia12MetricsAdapter.MeterName).ToArray();
            Assert.Contains(framework, instrument => instrument.Name == "avalonia.ui.measure.time");
            Assert.Contains(framework, instrument => instrument.Name == "avalonia.ui.arrange.time");
            Assert.Contains(framework, instrument => instrument.Name == "avalonia.comp.render.time");
            foreach (var name in new[] { "avalonia.ui.measure.time", "avalonia.ui.arrange.time", "avalonia.ui.render.time",
                         "avalonia.comp.update.time", "avalonia.comp.render.time" })
            {
                var instrument = Assert.Single(framework, item => item.Name == name);
                Assert.Equal("Histogram", instrument.Kind);
                Assert.Equal("ms", instrument.Unit);
                Assert.Contains(snapshot.Samples, sample => sample.InstrumentId == instrument.Id && sample.Value >= 0);
            }
            var runtimeIds = snapshot.Instruments.Where(instrument => instrument.MeterName == "System.Runtime")
                .Select(instrument => instrument.Id).ToHashSet();
            Assert.NotEmpty(runtimeIds);
            Assert.Contains(snapshot.Samples, sample => runtimeIds.Contains(sample.InstrumentId));
        }
        finally { window.Close(); }
    }

    private static SKBitmap Capture(Window window)
    {
        window.InvalidateVisual();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        using var memory = new MemoryStream();
        frame.Save(memory, new PngBitmapEncoderOptions());
        return SKBitmap.Decode(memory.ToArray());
    }

    private static int ChangedPixels(SKBitmap first, SKBitmap second)
    {
        Assert.Equal(first.Width, second.Width); Assert.Equal(first.Height, second.Height);
        var changed = 0;
        for (var y = 0; y < first.Height; y++)
            for (var x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) changed++;
        return changed;
    }
}
