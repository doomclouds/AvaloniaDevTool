using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private string _treeSearchQuery = "";
    private int _searchGeneration;
    private bool _followFocusEnabled;
    private TreeNodeInfo? _selectedSearchResult;
    private Bitmap? _elementImage;
    private string _elementImageSummary = "点击截取控件，生成目标控件当前的 PNG 快照。";
    public ObservableCollection<TreeNodeInfo> TreeSearchResults { get; } = [];
    public ObservableCollection<PseudoClassViewModel> PseudoClasses { get; } = [];
    public AsyncCommand SearchTreeCommand { get; private set; } = null!;
    public AsyncCommand FocusElementCommand { get; private set; } = null!;
    public AsyncCommand BringIntoViewCommand { get; private set; } = null!;
    public AsyncCommand EnableElementCommand { get; private set; } = null!;
    public AsyncCommand DisableElementCommand { get; private set; } = null!;
    public AsyncCommand CaptureElementCommand { get; private set; } = null!;
    public bool HasTreeSearchResults => TreeSearchResults.Count > 0;
    public Bitmap? ElementImage { get => _elementImage; private set => Set(ref _elementImage, value); }
    public string ElementImageSummary { get => _elementImageSummary; private set => Set(ref _elementImageSummary, value); }
    public string TreeSearchQuery { get => _treeSearchQuery; set => Set(ref _treeSearchQuery, value ?? ""); }
    public bool FollowFocusEnabled
    {
        get => _followFocusEnabled;
        set
        {
            if (!IsNativeBackend || !Set(ref _followFocusEnabled, value) || !IsConnected) return;
            _ = RunSafelyAsync(async () =>
            {
                try { await _client.SetFollowFocusAsync(value, _lifetime.Token); }
                catch
                {
                    _followFocusEnabled = false;
                    Raise(nameof(FollowFocusEnabled));
                    throw;
                }
            });
        }
    }
    public TreeNodeInfo? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set
        {
            if (Set(ref _selectedSearchResult, value) && value is not null && IsConnected)
            {
                var generation = ++_pickGeneration;
                _ = RunSafelyAsync(() => LocatePickedAsync(value.Id, generation));
            }
        }
    }

    private void InitializeExploration()
    {
        SearchTreeCommand = new AsyncCommand(SearchTreeAsync, ReportError, () => CanUseNativeTools);
        FocusElementCommand = new AsyncCommand(() => PerformElementActionAsync(ElementAction.Focus), ReportError, HasSelectedNativeElement);
        BringIntoViewCommand = new AsyncCommand(() => PerformElementActionAsync(ElementAction.BringIntoView), ReportError, HasSelectedNativeElement);
        EnableElementCommand = new AsyncCommand(() => PerformElementActionAsync(ElementAction.Enable), ReportError, HasSelectedNativeElement);
        DisableElementCommand = new AsyncCommand(() => PerformElementActionAsync(ElementAction.Disable), ReportError, HasSelectedNativeElement);
        CaptureElementCommand = new AsyncCommand(CaptureElementImageAsync, ReportError, HasSelectedElement);
    }

    private bool HasSelectedElement() => IsConnected && SelectedNode is { IsPlaceholder: false, IsSynthetic: false };
    private bool HasSelectedNativeElement() => CanUseNativeTools && SelectedNode is { IsPlaceholder: false, IsSynthetic: false };

    private void RefreshExplorationCommands()
    {
        SearchTreeCommand?.Refresh();
        FocusElementCommand?.Refresh();
        BringIntoViewCommand?.Refresh();
        EnableElementCommand?.Refresh();
        DisableElementCommand?.Refresh();
        CaptureElementCommand?.Refresh();
    }

    private async Task SearchTreeAsync()
    {
        if (!CanUseNativeTools) return;
        var generation = ++_searchGeneration;
        var kind = CurrentTreeKind;
        var sessionId = _backend.Session?.SessionId;
        TreeSearchResults.Clear();
        if (!string.IsNullOrWhiteSpace(TreeSearchQuery))
        {
            var result = await _client.SearchTreeAsync(new TreeSearchRequest(TreeSearchQuery, kind), _lifetime.Token);
            if (!IsConnected || generation != _searchGeneration || kind != CurrentTreeKind || sessionId != _backend.Session?.SessionId) return;
            foreach (var node in result.Matches) TreeSearchResults.Add(node);
            Status = $"搜索遍历 {result.Visited} 个对象，找到 {result.Matches.Count} 项" +
                (result.Truncated ? "；已达到结果或遍历上限，可缩小关键词。" : "。");
        }
        Raise(nameof(HasTreeSearchResults));
    }

    private async Task PerformElementActionAsync(ElementAction action)
    {
        if (!CanUseNativeTools) return;
        if (SelectedNode is not { IsPlaceholder: false, IsSynthetic: false } node) return;
        var result = await _client.PerformElementActionAsync(node.Id, action, _selectionCancellation?.Token ?? _lifetime.Token);
        Status = result.Succeeded ? $"已执行 {action}。" : result.Detail ?? $"{action} 未生效。";
    }

    private async Task RefreshExplorationPanelAsync(int generation, CancellationToken token)
    {
        if (!IsNativeBackend) return;
        if (SelectedNode is not { IsPlaceholder: false, IsSynthetic: false } node) return;
        var classes = await _client.GetPseudoClassesAsync(node.Id, token);
        if (!IsCurrentInspection(generation, token)) return;
        PseudoClasses.Clear();
        foreach (var item in classes)
            PseudoClasses.Add(new PseudoClassViewModel(item,
                async row =>
                {
                    await _client.SetPseudoClassAsync(node.Id, row.Name, !row.IsActive, token);
                    await RefreshExplorationPanelAsync(generation, token);
                },
                async row =>
                {
                    await _client.ResetPseudoClassAsync(node.Id, row.Name, token);
                    await RefreshExplorationPanelAsync(generation, token);
                }, ReportError));
    }

    private async Task CaptureElementImageAsync()
    {
        if (SelectedNode is not { IsPlaceholder: false, IsSynthetic: false } node) return;
        var token = _selectionCancellation?.Token ?? _lifetime.Token;
        var snapshot = await _backend.CaptureElementAsync(node.Id, token);
        token.ThrowIfCancellationRequested();
        if (SelectedNode?.Id != node.Id) return;
        using var bytes = new MemoryStream(Convert.FromBase64String(snapshot.PngBase64));
        var replacement = new Bitmap(bytes);
        ClearElementImage();
        ElementImage = replacement;
        ElementImageSummary = $"{snapshot.Width} × {snapshot.Height} PNG · {node.Description}";
    }

    private void ClearElementImage()
    {
        var previous = ElementImage;
        ElementImage = null;
        previous?.Dispose();
        ElementImageSummary = "点击截取控件，生成目标控件当前的 PNG 快照。";
    }

    private void ResetExplorationState()
    {
        ResetTreeSearch();
        _followFocusEnabled = false;
        Raise(nameof(FollowFocusEnabled));
        PseudoClasses.Clear();
        ClearElementImage();
    }

    private void ResetTreeSearch()
    {
        _searchGeneration++;
        TreeSearchResults.Clear();
        _selectedSearchResult = null;
        Raise(nameof(SelectedSearchResult));
        Raise(nameof(HasTreeSearchResults));
    }
}

public sealed class PseudoClassViewModel : ObservableObject
{
    public PseudoClassViewModel(PseudoClassInfo value, Func<PseudoClassViewModel, Task> toggle,
        Func<PseudoClassViewModel, Task> reset, Action<Exception> onError)
    {
        Name = value.Name;
        IsActive = value.IsActive;
        IsDeclared = value.IsDeclared;
        HasOverride = value.HasOverride;
        ToggleCommand = new AsyncCommand(() => toggle(this), onError);
        ResetCommand = new AsyncCommand(() => reset(this), onError, () => HasOverride);
    }
    public string Name { get; }
    public bool IsActive { get; }
    public bool IsDeclared { get; }
    public bool HasOverride { get; }
    public AsyncCommand ToggleCommand { get; }
    public AsyncCommand ResetCommand { get; }
}

