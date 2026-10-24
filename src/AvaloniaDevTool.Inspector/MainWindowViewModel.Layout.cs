using System.Collections.ObjectModel;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private LayoutSnapshot? _layoutSnapshot;
    private LayoutLayer? _selectedLayoutLayer;
    private long? _selectedLayoutObjectId;
    private double _layerSpacing = 18;
    private int _layoutNodeLimit = 128;
    private int _layoutResetVersion;
    private bool _syncingLayoutSelection;
    private string _layoutSummary = "选择控件后刷新，或激活此页采集所选视觉子树。";
    public ObservableCollection<LayoutLayer> LayoutLayers { get; } = [];
    public IReadOnlyList<int> LayoutNodeLimits { get; } = [64, 128, 256];
    public LayoutSnapshot? LayoutSceneSnapshot { get => _layoutSnapshot; private set => Set(ref _layoutSnapshot, value); }
    public string LayoutSummary { get => _layoutSummary; private set => Set(ref _layoutSummary, value); }
    public double LayerSpacing { get => _layerSpacing; set => Set(ref _layerSpacing, Math.Clamp(value, 0, 80)); }
    public int LayoutNodeLimit { get => _layoutNodeLimit; set => Set(ref _layoutNodeLimit, Math.Clamp(value, 1, 256)); }
    public int LayoutResetVersion { get => _layoutResetVersion; private set => Set(ref _layoutResetVersion, value); }
    public AsyncCommand ResetLayoutCameraCommand { get; private set; } = null!;
    public long? SelectedLayoutObjectId
    {
        get => _selectedLayoutObjectId;
        set
        {
            if (!Set(ref _selectedLayoutObjectId, value)) return;
            _syncingLayoutSelection = true;
            try
            {
                _selectedLayoutLayer = LayoutLayers.FirstOrDefault(layer => layer.ObjectId == value);
                Raise(nameof(SelectedLayoutLayer));
            }
            finally { _syncingLayoutSelection = false; }
            if (value is { } id && id != SelectedNode?.Id && CanUseNativeTools)
            {
                var generation = ++_pickGeneration;
                _ = RunSafelyAsync(() => LocatePickedAsync(id, generation));
            }
        }
    }
    public LayoutLayer? SelectedLayoutLayer
    {
        get => _selectedLayoutLayer;
        set
        {
            if (Set(ref _selectedLayoutLayer, value) && !_syncingLayoutSelection)
                SelectedLayoutObjectId = value?.ObjectId;
        }
    }

    private void InitializeLayout() => ResetLayoutCameraCommand =
        new AsyncCommand(() => { LayoutResetVersion++; return Task.CompletedTask; }, ReportError);

    private async Task RefreshLayoutAsync(int generation, CancellationToken token)
    {
        if (IsSyntheticSelection) return;
        var objectId = SelectedNode is { IsPlaceholder: false } node ? node.Id : Roots.FirstOrDefault()?.Id;
        if (objectId is null)
        {
            LayoutSummary = "当前没有可采集的窗口根。";
            return;
        }
        var snapshot = await _client.GetLayoutSnapshotAsync(objectId.Value, LayoutNodeLimit, 12, token);
        if (!IsCurrentInspection(generation, token)) return;
        LayoutSceneSnapshot = snapshot;
        Replace(LayoutLayers, snapshot.Layers);
        SyncLayoutSelectionFromTree();
        LayoutSummary = $"3D 层级几何 · {snapshot.CaptureKind} · 根 #{snapshot.RootObjectId} · {snapshot.Layers.Count} 层 / 访问 {snapshot.Visited} 个节点" +
            (snapshot.Truncated ? $" · 已按 {LayoutNodeLimit} 节点 / 12 层深度截断" : "") +
            "。仅采集视觉几何，不是逐层像素快照。";
    }

    private void SyncLayoutSelectionFromTree()
    {
        _syncingLayoutSelection = true;
        try
        {
            _selectedLayoutObjectId = SelectedNode?.Id is { } id && LayoutLayers.Any(layer => layer.ObjectId == id) ? id : null;
            _selectedLayoutLayer = LayoutLayers.FirstOrDefault(layer => layer.ObjectId == _selectedLayoutObjectId);
            Raise(nameof(SelectedLayoutObjectId));
            Raise(nameof(SelectedLayoutLayer));
        }
        finally { _syncingLayoutSelection = false; }
    }

    private Task RefreshSelectionInspectionAsync()
    {
        if (InspectionTabIndex == 8)
        {
            SyncLayoutSelectionFromTree();
            return Task.CompletedTask;
        }
        return RefreshActiveInspectionAsync();
    }

    private void ResetLayout()
    {
        LayoutSceneSnapshot = null;
        LayoutLayers.Clear();
        _selectedLayoutLayer = null;
        _selectedLayoutObjectId = null;
        Raise(nameof(SelectedLayoutLayer));
        Raise(nameof(SelectedLayoutObjectId));
        LayoutSummary = "连接应用并选择控件后，刷新其视觉子树几何。";
    }
}

