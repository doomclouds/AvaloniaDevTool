using System.Collections.ObjectModel;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private RendererDiagnosticsStatusDto? _rendererDiagnostics;
    private RendererWindowOption? _selectedRendererWindow;
    private DispatcherTimer? _rendererDiagnosticsTimer;
    private bool _rendererRefreshing;
    private bool _rendererFps, _rendererDirtyRects, _rendererLayoutTime, _rendererRenderTime, _rendererFollowNewWindows;
    public ObservableCollection<RendererWindowOption> RendererWindows { get; } = [];
    public AsyncCommand ApplyRendererDiagnosticsCommand { get; private set; } = null!;
    public AsyncCommand StopRendererDiagnosticsCommand { get; private set; } = null!;
    public AsyncCommand RefreshRendererDiagnosticsCommand { get; private set; } = null!;
    public RendererDiagnosticsStatusDto? CurrentRendererDiagnostics => _rendererDiagnostics;
    public RendererWindowOption? SelectedRendererWindow
    {
        get => _selectedRendererWindow;
        set
        {
            if (!Set(ref _selectedRendererWindow, value)) return;
            Raise(nameof(CanFollowNewRendererWindows));
            RefreshRendererDiagnosticsCommands();
        }
    }
    public bool CanFollowNewRendererWindows => SelectedRendererWindow?.Id is null;
    public bool RendererFps { get => _rendererFps; set => Set(ref _rendererFps, value); }
    public bool RendererDirtyRects { get => _rendererDirtyRects; set => Set(ref _rendererDirtyRects, value); }
    public bool RendererLayoutTime { get => _rendererLayoutTime; set => Set(ref _rendererLayoutTime, value); }
    public bool RendererRenderTime { get => _rendererRenderTime; set => Set(ref _rendererRenderTime, value); }
    public bool RendererFollowNewWindows { get => _rendererFollowNewWindows; set => Set(ref _rendererFollowNewWindows, value); }
    public string RendererDiagnosticsSummary => _rendererDiagnostics is { } state
        ? $"{(state.IsActive ? "覆盖层已开启" : "覆盖层已停止")} · {state.Windows.Count} 个窗口 · 来源 {state.Source}\n" +
          string.Join("\n", state.Windows.Select(window => $"#{window.WindowId} {window.Title}: {window.CurrentFlags}" +
              (window.HasOverride ? $" · 本工具新增 {window.AddedFlags}" : "") + (window.HasConflict ? " · 应用已改写，保留应用值" : ""))) +
          (state.Conflicts.Count == 0 ? "" : "\n" + string.Join("\n", state.Conflicts))
        : "连接后可为指定窗口开启 Avalonia 原生 FPS、失效区域和耗时图。";

    private void InitializeRendererDiagnostics()
    {
        ApplyRendererDiagnosticsCommand = new AsyncCommand(ApplyRendererDiagnosticsAsync, ReportError,
            () => CanUseNativeTools && SelectedRendererWindow is not null);
        StopRendererDiagnosticsCommand = new AsyncCommand(StopRendererDiagnosticsAsync, ReportError,
            () => CanUseNativeTools && _rendererDiagnostics?.IsActive == true);
        RefreshRendererDiagnosticsCommand = new AsyncCommand(RefreshRendererDiagnosticsAsync, ReportError, () => CanUseNativeTools);
        _rendererDiagnosticsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _rendererDiagnosticsTimer.Tick += OnRendererDiagnosticsTick;
        _rendererDiagnosticsTimer.Start();
    }

    private void RefreshRendererDiagnosticsCommands()
    {
        ApplyRendererDiagnosticsCommand?.Refresh();
        StopRendererDiagnosticsCommand?.Refresh();
        RefreshRendererDiagnosticsCommand?.Refresh();
    }

    public async Task RefreshRendererDiagnosticsAsync()
    {
        if (!CanUseNativeTools || _rendererRefreshing) return;
        _rendererRefreshing = true;
        var session = _backend.Session?.SessionId;
        try
        {
            var state = await _client.GetRendererDiagnosticsAsync(_lifetime.Token);
            if (CanUseNativeTools && session == _backend.Session?.SessionId && !_disposed) SetRendererDiagnostics(state);
        }
        finally { _rendererRefreshing = false; }
    }

    private async Task ApplyRendererDiagnosticsAsync()
    {
        var flags = (RendererFps ? RendererOverlayFlags.Fps : 0) |
            (RendererDirtyRects ? RendererOverlayFlags.DirtyRects : 0) |
            (RendererLayoutTime ? RendererOverlayFlags.LayoutTimeGraph : 0) |
            (RendererRenderTime ? RendererOverlayFlags.RenderTimeGraph : 0);
        SetRendererDiagnostics(await _client.SetRendererDiagnosticsAsync(new RendererDiagnosticsSetRequest(
            SelectedRendererWindow?.Id, flags, CanFollowNewRendererWindows && RendererFollowNewWindows), _lifetime.Token));
    }

    private async Task StopRendererDiagnosticsAsync() =>
        SetRendererDiagnostics(await _client.StopRendererDiagnosticsAsync(_lifetime.Token));

    private void SetRendererDiagnostics(RendererDiagnosticsStatusDto state)
    {
        _rendererDiagnostics = state;
        var selectedId = SelectedRendererWindow?.Id;
        var options = new[] { new RendererWindowOption(null, "全部窗口") }
            .Concat(state.Windows.Select(window => new RendererWindowOption(window.WindowId, $"#{window.WindowId} {window.Title}"))).ToArray();
        if (!RendererWindows.SequenceEqual(options))
        {
            Replace(RendererWindows, options);
            SelectedRendererWindow = RendererWindows.FirstOrDefault(window => window.Id == selectedId) ?? RendererWindows[0];
        }
        Raise(nameof(CurrentRendererDiagnostics));
        Raise(nameof(RendererDiagnosticsSummary));
        RefreshRendererDiagnosticsCommands();
    }

    private void ResetRendererDiagnostics()
    {
        _rendererDiagnostics = null;
        RendererWindows.Clear();
        SelectedRendererWindow = null;
        RendererFps = RendererDirtyRects = RendererLayoutTime = RendererRenderTime = RendererFollowNewWindows = false;
        Raise(nameof(CurrentRendererDiagnostics));
        Raise(nameof(RendererDiagnosticsSummary));
        RefreshRendererDiagnosticsCommands();
    }

    private void OnRendererDiagnosticsTick(object? sender, EventArgs args)
    {
        if (CanUseNativeTools) _ = RunSafelyAsync(RefreshRendererDiagnosticsAsync);
    }

    private void DisposeRendererDiagnostics()
    {
        if (_rendererDiagnosticsTimer is not { } timer) return;
        timer.Stop();
        timer.Tick -= OnRendererDiagnosticsTick;
    }
}

public sealed record RendererWindowOption(long? Id, string Title)
{
    public override string ToString() => Title;
}
