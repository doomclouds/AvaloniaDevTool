using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private bool _overlayBounds = true, _overlayMargin, _overlayPadding, _overlayInfo, _overlayRulers, _overlayGuidelines;
    private OverlaySnapshot? _overlaySnapshot;
    private Task? _overlayConfigurationTask, _treeHoverTask;
    private CancellationTokenSource? _overlayCancellation;
    private long _overlayEpoch, _treeHoverRevision;
    private long? _treeHoverId;
    private bool _overlayConfigurationDirty, _treeHoverDirty;
    private OverlaySnapshot? _boxModelSnapshot;
    private long _boxModelRevision;
    private long? _boxHoverObjectId;
    private string? _boxHoverRegion, _boxHoverSide;
    private bool _boxModelOverrideActive;
    public bool OverlayBounds { get => _overlayBounds; set { if (Set(ref _overlayBounds, value)) QueueOverlayConfiguration(); } }
    public bool OverlayMargin { get => _overlayMargin; set { if (Set(ref _overlayMargin, value)) QueueOverlayConfiguration(); } }
    public bool OverlayPadding { get => _overlayPadding; set { if (Set(ref _overlayPadding, value)) QueueOverlayConfiguration(); } }
    public bool OverlayInfo { get => _overlayInfo; set { if (Set(ref _overlayInfo, value)) QueueOverlayConfiguration(); } }
    public bool OverlayRulers { get => _overlayRulers; set { if (Set(ref _overlayRulers, value)) QueueOverlayConfiguration(); } }
    public bool OverlayGuidelines { get => _overlayGuidelines; set { if (Set(ref _overlayGuidelines, value)) QueueOverlayConfiguration(); } }
    public OverlaySnapshot? CurrentOverlaySnapshot => _overlaySnapshot;
    public OverlaySnapshot? BoxModelSnapshot => _boxModelSnapshot;
    public string OverlaySummary => _overlaySnapshot is { IsActive: true } snapshot
        ? $"对象 #{snapshot.ObjectId} · 根 #{snapshot.RootObjectId} · {snapshot.CoordinateSpace} · 渲染缩放 {snapshot.RenderScaling:G4}\n" +
          (snapshot.Info?.Text ?? "") + $"\n{snapshot.Regions.Count} 个盒模型区域 · {snapshot.Rulers.Count} 条标尺 · {snapshot.Guidelines.Count} 条引导线" +
          (snapshot.Limitations.Count == 0 ? "" : "\n" + string.Join("\n", snapshot.Limitations))
        : "在树中选择或悬停真实控件，或点击“显示所选控件”。测量单位为根坐标 DIP，设备像素单独标注。";
    public AsyncCommand ShowOverlayCommand { get; private set; } = null!;
    public AsyncCommand HideOverlayCommand { get; private set; } = null!;
    public AsyncCommand RefreshOverlayCommand { get; private set; } = null!;
    private OverlayOptions OverlayPreferences() => new(OverlayBounds, OverlayMargin, OverlayPadding, OverlayInfo, OverlayRulers,
        OverlayGuidelines, _boxHoverRegion, _boxHoverSide);

    private void InitializeOverlay()
    {
        _overlayCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        ShowOverlayCommand = new AsyncCommand(async () =>
        {
            if (SelectedNode is not { IsSynthetic: false, IsPlaceholder: false } node) return;
            HighlightEnabled = true;
            var epoch = _overlayEpoch;
            var revision = _boxModelRevision;
            var result = await _client.ConfigureOverlayAsync(OverlayPreferences(), node.Id, OverlayToken());
            if (OverlayResponseIsCurrent(epoch) && revision == _boxModelRevision) SetOverlaySnapshot(result);
        }, ReportError, HasSelectedNativeElement);
        HideOverlayCommand = new AsyncCommand(async () =>
        {
            HighlightEnabled = false;
            await _client.HighlightAsync(null, OverlayToken());
            await RefreshOverlayAsync();
        }, ReportError, () => CanUseNativeTools);
        RefreshOverlayCommand = new AsyncCommand(RefreshOverlayAsync, ReportError, () => CanUseNativeTools);
    }
    private CancellationToken OverlayToken() => _overlayCancellation?.Token ?? _lifetime.Token;
    private bool OverlayResponseIsCurrent(long epoch) => !_disposed && CanUseNativeTools && epoch == _overlayEpoch;
    private void RefreshOverlayCommands()
    {
        ShowOverlayCommand?.Refresh(); HideOverlayCommand?.Refresh(); RefreshOverlayCommand?.Refresh();
    }
    private void QueueOverlayConfiguration()
    {
        _overlayConfigurationDirty = true;
        if (!CanUseNativeTools || _disposed || _overlayConfigurationTask is { IsCompleted: false }) return;
        _overlayConfigurationTask = RunSafelyAsync(ApplyOverlayConfigurationLoopAsync);
    }
    private async Task ApplyOverlayConfigurationLoopAsync()
    {
        var epoch = _overlayEpoch;
        var token = OverlayToken();
        while (_overlayConfigurationDirty && OverlayResponseIsCurrent(epoch))
        {
            _overlayConfigurationDirty = false;
            var revision = _boxModelRevision;
            var restore = _boxModelOverrideActive;
            var target = _boxHoverObjectId ?? (restore && SelectedNode is { IsSynthetic: false, IsPlaceholder: false } node ? node.Id : (long?)null);
            var result = await _client.ConfigureOverlayAsync(OverlayPreferences(), target, token);
            if (!OverlayResponseIsCurrent(epoch) || revision != _boxModelRevision) continue;
            if (restore && _boxHoverRegion is null)
            {
                _boxModelOverrideActive = false;
                if (!HighlightEnabled || SelectedNode is not { IsSynthetic: false, IsPlaceholder: false })
                {
                    await _client.HighlightAsync(null, token);
                    result = await _client.GetOverlaySnapshotAsync(cancellationToken: token);
                }
            }
            if (OverlayResponseIsCurrent(epoch) && revision == _boxModelRevision) SetOverlaySnapshot(result);
        }
    }
    public async Task RefreshOverlayAsync()
    {
        if (!CanUseNativeTools || _disposed) return;
        var epoch = _overlayEpoch;
        var revision = _boxModelRevision;
        var result = await _client.GetOverlaySnapshotAsync(cancellationToken: OverlayToken());
        if (OverlayResponseIsCurrent(epoch) && revision == _boxModelRevision) SetOverlaySnapshot(result);
        await RefreshBoxModelAsync();
    }
    private void SetOverlaySnapshot(OverlaySnapshot snapshot)
    {
        _overlaySnapshot = snapshot;
        Raise(nameof(CurrentOverlaySnapshot)); Raise(nameof(OverlaySummary));
        if (snapshot.IsActive && SelectedNode is { IsSynthetic: false, IsPlaceholder: false } node && snapshot.ObjectId == node.Id)
        {
            _boxModelSnapshot = snapshot;
            Raise(nameof(BoxModelSnapshot));
        }
    }

    private void OverlaySelectionChanged()
    {
        _boxModelRevision++;
        _boxHoverObjectId = null; _boxHoverRegion = null; _boxHoverSide = null;
        _treeHoverId = null; _treeHoverRevision++;
        _boxModelSnapshot = null;
        Raise(nameof(BoxModelSnapshot));
        if (_boxModelOverrideActive) QueueOverlayConfiguration();
        _ = RunSafelyAsync(RefreshBoxModelAsync);
    }

    private async Task RefreshBoxModelAsync()
    {
        if (!CanUseNativeTools || _disposed || SelectedNode is not { IsSynthetic: false, IsPlaceholder: false } node) return;
        var epoch = _overlayEpoch;
        var revision = _boxModelRevision;
        var snapshot = await _client.GetOverlaySnapshotAsync(node.Id, OverlayToken());
        if (!OverlayResponseIsCurrent(epoch) || revision != _boxModelRevision || node.Id != SelectedNode?.Id) return;
        _boxModelSnapshot = snapshot;
        Raise(nameof(BoxModelSnapshot));
    }

    public Task PreviewBoxModelRegionAsync(long objectId, string? region, string? side = null,
        string? leavingRegion = null, string? leavingSide = null)
    {
        if (!CanUseNativeTools || _disposed || _isPicking || SelectedNode?.Id != objectId) return Task.CompletedTask;
        if (region is null && (_boxHoverObjectId != objectId ||
            leavingRegion is not null && (_boxHoverRegion != leavingRegion || _boxHoverSide != leavingSide))) return Task.CompletedTask;
        if (region == _boxHoverRegion && side == _boxHoverSide && _boxHoverObjectId == objectId) return _overlayConfigurationTask ?? Task.CompletedTask;
        _boxModelRevision++;
        _boxHoverRegion = region; _boxHoverSide = side;
        _boxHoverObjectId = region is null ? null : objectId;
        _boxModelOverrideActive = true;
        _treeHoverId = null; _treeHoverRevision++;
        QueueOverlayConfiguration();
        return _overlayConfigurationTask ?? Task.CompletedTask;
    }

    public Task PreviewTreeHighlightAsync(long? objectId, long? leavingObjectId = null)
    {
        if (!CanUseNativeTools || _disposed || _isPicking) return Task.CompletedTask;
        if (objectId is null && leavingObjectId is { } leaving && _treeHoverId != leaving) return Task.CompletedTask;
        _treeHoverId = objectId;
        _treeHoverRevision++;
        _treeHoverDirty = true;
        if (_treeHoverTask is null || _treeHoverTask.IsCompleted) _treeHoverTask = RunSafelyAsync(TreeHoverLoopAsync);
        return _treeHoverTask;
    }
    private async Task TreeHoverLoopAsync()
    {
        var epoch = _overlayEpoch;
        var token = OverlayToken();
        while (_treeHoverDirty && OverlayResponseIsCurrent(epoch))
        {
            _treeHoverDirty = false;
            var revision = _treeHoverRevision;
            var hover = _treeHoverId;
            if (hover is null)
            {
                await Task.Delay(70, token);
                if (revision != _treeHoverRevision) continue;
            }
            if (!OverlayResponseIsCurrent(epoch) || _isPicking || _boxHoverRegion is not null) return;
            var selected = SelectedNode is { IsSynthetic: false, IsPlaceholder: false } node ? node.Id : (long?)null;
            await _client.HighlightAsync(HighlightEnabled ? hover ?? selected : null, token);
            if (_boxHoverRegion is not null) QueueOverlayConfiguration();
        }
    }
    private void ResetOverlay()
    {
        _overlayEpoch++;
        _overlayCancellation?.Cancel(); _overlayCancellation?.Dispose();
        _overlayCancellation = _disposed ? null : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _overlayConfigurationTask = null; _treeHoverTask = null;
        _treeHoverId = null; _treeHoverDirty = false; _overlayConfigurationDirty = true;
        _boxModelRevision++; _boxHoverObjectId = null; _boxHoverRegion = null; _boxHoverSide = null;
        _boxModelOverrideActive = false; _boxModelSnapshot = null;
        _overlaySnapshot = null;
        Raise(nameof(CurrentOverlaySnapshot)); Raise(nameof(OverlaySummary)); Raise(nameof(BoxModelSnapshot)); RefreshOverlayCommands();
    }
    private void DisposeOverlay() => ResetOverlay();
}
