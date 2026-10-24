using System.Collections.ObjectModel;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private CompositionScene? _compositionScene;
    private CancellationTokenSource? _compositionLoad;
    private long _compositionGeneration;
    private bool _compositionCapturing, _syncingComposition;
    private long? _compositionRequestedObjectId, _selectedCompositionCaptureId;
    private CompositionLayerRow? _selectedCompositionLayer;
    private string _compositionSummary = "选择控件后采集合成层自身的绘制图像。";
    private bool _compositionBorders = true, _compositionGradients;
    private int _compositionMinDepth, _compositionMaxDepth = 64;
    private int _layerSceneMode;
    private int _compositionFrontVersion;

    public ObservableCollection<CompositionLayerRow> CompositionLayers { get; } = [];
    public CompositionScene? CapturedCompositionScene { get => _compositionScene; private set => Set(ref _compositionScene, value); }
    public string CompositionSummary { get => _compositionSummary; private set => Set(ref _compositionSummary, value); }
    public AsyncCommand CaptureCompositionCommand { get; private set; } = null!;
    public AsyncCommand FrontCompositionCommand { get; private set; } = null!;
    public int CompositionFrontVersion { get => _compositionFrontVersion; private set => Set(ref _compositionFrontVersion, value); }
    public bool CompositionBorders { get => _compositionBorders; set => Set(ref _compositionBorders, value); }
    public bool CompositionGradients { get => _compositionGradients; set => Set(ref _compositionGradients, value); }
    public int CompositionMinDepth { get => _compositionMinDepth; set => Set(ref _compositionMinDepth, Math.Clamp(value, 0, 64)); }
    public int CompositionMaxDepth { get => _compositionMaxDepth; set => Set(ref _compositionMaxDepth, Math.Clamp(value, 0, 64)); }
    public int LayerSceneMode
    {
        get => _layerSceneMode;
        set
        {
            if (!Set(ref _layerSceneMode, value == 1 ? 1 : 0)) return;
            if (value != 1) CancelCompositionLoad();
            if (InspectionTabIndex == 8 && CanUseNativeTools) _ = RunSafelyAsync(RefreshActiveInspectionAsync);
        }
    }
    public CompositionLayerRow? SelectedCompositionLayer
    {
        get => _selectedCompositionLayer;
        set
        {
            if (!Set(ref _selectedCompositionLayer, value) || _syncingComposition) return;
            SelectedCompositionCaptureId = value?.CaptureId;
        }
    }
    public long? SelectedCompositionCaptureId
    {
        get => _selectedCompositionCaptureId;
        set
        {
            if (!Set(ref _selectedCompositionCaptureId, value)) return;
            _syncingComposition = true;
            try { SelectedCompositionLayer = CompositionLayers.FirstOrDefault(item => item.CaptureId == value); }
            finally { _syncingComposition = false; }
            if (SelectedCompositionLayer?.Layer.ObjectId is { } objectId && SelectedNode?.Id != objectId && CanUseNativeTools)
                _ = RunSafelyAsync(() => LocatePickedAsync(objectId, ++_pickGeneration));
        }
    }

    private void InitializeComposition()
    {
        CaptureCompositionCommand = new AsyncCommand(RefreshCompositionAsync, ReportError,
            () => CanUseNativeTools && !IsSyntheticSelection && !_compositionCapturing);
        FrontCompositionCommand = new AsyncCommand(() => { CompositionFrontVersion++; return Task.CompletedTask; }, ReportError);
    }
    private void RefreshCompositionCommands() => CaptureCompositionCommand?.Refresh();

    public async Task RefreshCompositionAsync()
    {
        if (!CanUseNativeTools || IsSyntheticSelection || _compositionCapturing) return;
        var objectId = SelectedNode?.Id ?? Roots.FirstOrDefault()?.Id;
        if (objectId is null) { CompositionSummary = "当前没有可采集的窗口根。"; return; }
        CancelCompositionLoad();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _compositionLoad = cancellation;
        var generation = _compositionGeneration;
        var sessionId = _backend.Session?.SessionId;
        _compositionRequestedObjectId = objectId;
        _compositionCapturing = true;
        RefreshCompositionCommands();
        try
        {
            var snapshot = await _client.GetCompositionSnapshotAsync(new CompositionSnapshotRequest(objectId.Value, LayoutNodeLimit), cancellation.Token);
            if (!CompositionResponseIsCurrent(generation, sessionId) || cancellation.IsCancellationRequested) return;
            var scene = new CompositionScene(snapshot);
            var previous = CapturedCompositionScene;
            CapturedCompositionScene = scene;
            previous?.Dispose();
            Replace(CompositionLayers, snapshot.Layers.Select(layer => new CompositionLayerRow(layer)));
            SyncCompositionSelectionFromTree();
            CompositionSummary = $"{snapshot.CaptureKind} · {snapshot.Layers.Count} 层 · {scene.Images.Count} 张自身绘制 PNG" +
                (snapshot.Truncated ? " · 遍历已截断" : "") +
                (snapshot.SupportsExactComposite ? " · 支持当前基础组合" : " · 存在组合限制") +
                (snapshot.Limitations.Count == 0 ? "" : "\n" + string.Join("\n", snapshot.Limitations));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_compositionLoad, cancellation)) _compositionCapturing = false;
            RefreshCompositionCommands();
        }
    }

    private bool CompositionResponseIsCurrent(long generation, string? sessionId) =>
        !_disposed && CanUseNativeTools && generation == _compositionGeneration && sessionId == _backend.Session?.SessionId;

    private void SyncCompositionSelectionFromTree()
    {
        if (_compositionCapturing && SelectedNode?.Id != _compositionRequestedObjectId) CancelCompositionLoad();
        _syncingComposition = true;
        try
        {
            _selectedCompositionLayer = CompositionLayers.FirstOrDefault(item => item.Layer.ObjectId == SelectedNode?.Id);
            _selectedCompositionCaptureId = _selectedCompositionLayer?.CaptureId;
            Raise(nameof(SelectedCompositionLayer)); Raise(nameof(SelectedCompositionCaptureId));
        }
        finally { _syncingComposition = false; }
    }

    private void CancelCompositionLoad()
    {
        _compositionGeneration++;
        _compositionLoad?.Cancel(); _compositionLoad?.Dispose(); _compositionLoad = null;
        _compositionCapturing = false;
        RefreshCompositionCommands();
    }
    private void ResetComposition()
    {
        CancelCompositionLoad();
        var scene = CapturedCompositionScene;
        CapturedCompositionScene = null;
        scene?.Dispose();
        CompositionLayers.Clear();
        _selectedCompositionLayer = null; _selectedCompositionCaptureId = null;
        Raise(nameof(SelectedCompositionLayer)); Raise(nameof(SelectedCompositionCaptureId));
        CompositionSummary = "选择控件后采集合成层自身的绘制图像。";
    }
    private void DisposeComposition() => ResetComposition();
}

public sealed class CompositionLayerRow(CompositionLayer layer)
{
    public CompositionLayer Layer => layer;
    public long CaptureId => layer.CaptureId;
    public string Title => $"层 {layer.CaptureId} · {(layer.ObjectId is { } id ? $"控件 #{id}" : "合成器节点")} · {layer.TypeName.Split('.').Last()}";
    public string Name => layer.Name ?? "";
    public string Details => $"Depth {layer.Depth} · 顺序 {layer.ChildIndex} · ZIndex {layer.ZIndex?.ToString() ?? "未知"} · opacity {layer.Opacity:G3}\n{layer.ImageStatus} · {layer.PixelWidth}×{layer.PixelHeight}";
}
