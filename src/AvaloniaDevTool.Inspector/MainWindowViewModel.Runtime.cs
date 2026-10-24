using System.Collections.ObjectModel;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private const int RuntimePageSize = 50;
    private const int RuntimeMaximumOffset = 10_000;
    private readonly List<RuntimeLocation> _runtimePath = [];
    private CancellationTokenSource? _runtimeCancellation;
    private long _runtimeGeneration;
    private long? _runtimeRootSelectionId;
    private int _runtimeSectionIndex;
    private int _runtimeTotalCount;
    private bool _runtimeHasMore;
    private bool _runtimeIsLoading;
    private bool _runtimeCollectionSupported;
    private string _runtimeTitle = "选择一个控件";
    private string _runtimeSummary = "激活此页后按需读取所选控件的公共 CLR 成员。";
    private string _runtimeError = "";

    public ObservableCollection<RuntimeRowViewModel> RuntimeRows { get; } = [];
    public ObservableCollection<RuntimeBreadcrumbViewModel> RuntimeBreadcrumbs { get; } = [];
    public IReadOnlyList<string> RuntimeSections { get; } = ["公共成员 / 字段", "集合元素"];
    public AsyncCommand RefreshRuntimeCommand { get; private set; } = null!;
    public AsyncCommand RuntimeUseSelectionCommand { get; private set; } = null!;
    public AsyncCommand RuntimeBackCommand { get; private set; } = null!;
    public AsyncCommand RuntimePreviousPageCommand { get; private set; } = null!;
    public AsyncCommand RuntimeNextPageCommand { get; private set; } = null!;
    public string RuntimeTitle { get => _runtimeTitle; private set => Set(ref _runtimeTitle, value); }
    public string RuntimeSummary { get => _runtimeSummary; private set => Set(ref _runtimeSummary, value); }
    public string RuntimeError
    {
        get => _runtimeError;
        private set
        {
            if (!Set(ref _runtimeError, value)) return;
            Raise(nameof(RuntimeHasError));
            Raise(nameof(RuntimePageText));
        }
    }
    public bool RuntimeHasError => RuntimeError.Length > 0;
    public bool RuntimeIsLoading
    {
        get => _runtimeIsLoading;
        private set { if (Set(ref _runtimeIsLoading, value)) Raise(nameof(RuntimePageText)); }
    }
    public bool RuntimeCollectionSupported { get => _runtimeCollectionSupported; private set => Set(ref _runtimeCollectionSupported, value); }
    public long? RuntimeObjectId => _runtimePath.LastOrDefault()?.ObjectId;
    public string RuntimePageText => _runtimePath.LastOrDefault() is { } location
        ? $"第 {location.Offset / RuntimePageSize + 1} 页 · " +
          (RuntimeIsLoading ? "读取中" : RuntimeHasError ? "读取失败" :
              location.Section == 1 && !RuntimeCollectionSupported ? "集合未枚举" : $"{RuntimeRows.Count} / {_runtimeTotalCount} 项")
        : "尚未读取";

    public int RuntimeSectionIndex
    {
        get => _runtimeSectionIndex;
        set
        {
            if (value is < 0 or > 1 || !Set(ref _runtimeSectionIndex, value)) return;
            if (_runtimePath.LastOrDefault() is { } location) location.Section = value;
            _ = RunSafelyAsync(RefreshRuntimeAsync);
        }
    }

    private void InitializeRuntime()
    {
        RefreshRuntimeCommand = new AsyncCommand(RefreshRuntimeAsync, ReportError, CanReadRuntime);
        RuntimeUseSelectionCommand = new AsyncCommand(async () =>
        {
            ResetRuntimeSelection();
            await RefreshRuntimeAsync();
        }, ReportError, CanReadRuntime);
        RuntimeBackCommand = new AsyncCommand(() => NavigateRuntimeBackAsync(_runtimePath.Count - 2), ReportError,
            () => CanReadRuntime() && _runtimePath.Count > 1);
        RuntimePreviousPageCommand = new AsyncCommand(() => ChangeRuntimePageAsync(-RuntimePageSize), ReportError,
            () => CanReadRuntime() && _runtimePath.LastOrDefault()?.Offset > 0);
        RuntimeNextPageCommand = new AsyncCommand(() => ChangeRuntimePageAsync(RuntimePageSize), ReportError,
            () => CanReadRuntime() && _runtimeHasMore && _runtimePath.LastOrDefault()?.Offset + RuntimePageSize <= RuntimeMaximumOffset);
    }

    private bool CanReadRuntime() => CanUseNativeTools && !_disposed && InspectionTabIndex == 9 &&
        SelectedNode is { IsPlaceholder: false, IsSynthetic: false };

    private void RefreshRuntimeCommands()
    {
        RefreshRuntimeCommand?.Refresh();
        RuntimeUseSelectionCommand?.Refresh();
        RuntimeBackCommand?.Refresh();
        RuntimePreviousPageCommand?.Refresh();
        RuntimeNextPageCommand?.Refresh();
        foreach (var row in RuntimeRows) row.RefreshCommands();
        foreach (var crumb in RuntimeBreadcrumbs) crumb.OpenCommand.Refresh();
    }

    private void RuntimePageChanged()
    {
        CancelRuntimeLoad();
        RefreshRuntimeCommands();
    }

    private void RuntimeSelectionChanged()
    {
        BindingNavigationSelectionChanged();
        var selectionId = SelectedNode is { IsPlaceholder: false, IsSynthetic: false } node ? node.Id : (long?)null;
        if (_runtimeRootSelectionId != selectionId)
            ResetRuntimeSelection();
        if (IsSyntheticSelection) RuntimeSummary = SyntheticSelectionNotice;
        RefreshRuntimeCommands();
    }

    private void ResetRuntimeSelection()
    {
        CancelRuntimeLoad();
        _runtimeRootSelectionId = SelectedNode is { IsPlaceholder: false, IsSynthetic: false } node ? node.Id : null;
        _runtimePath.Clear();
        if (_runtimeRootSelectionId is { } id)
            _runtimePath.Add(new RuntimeLocation(id, SelectedNode!.TypeName));
        RuntimeRows.Clear();
        RuntimeCollectionSupported = false;
        RuntimeError = "";
        _runtimeHasMore = false;
        _runtimeTotalCount = 0;
        _runtimeSectionIndex = 0;
        Raise(nameof(RuntimeSectionIndex));
        RuntimeTitle = "选择一个控件";
        RuntimeSummary = _runtimeRootSelectionId is null ? "请先在控件树中选择一个控件。" : "激活对象页或刷新后读取公共 CLR 成员；从 DataContext 可进入业务对象。";
        UpdateRuntimeNavigation();
    }

    private void SetRuntimeBindingRoot(long objectId, string label)
    {
        ResetRuntimeSelection();
        if (_runtimePath.LastOrDefault()?.ObjectId != objectId)
            _runtimePath.Add(new RuntimeLocation(objectId, label));
        RuntimeRows.Clear();
        UpdateRuntimeNavigation();
    }

    private void CancelRuntimeLoad()
    {
        _runtimeGeneration++;
        _runtimeCancellation?.Cancel();
        _runtimeCancellation?.Dispose();
        _runtimeCancellation = null;
        RuntimeIsLoading = false;
    }

    public async Task RefreshRuntimeAsync()
    {
        if (!CanReadRuntime()) return;
        if (_runtimeRootSelectionId != SelectedNode!.Id || _runtimePath.Count == 0)
            ResetRuntimeSelection();
        var location = _runtimePath[^1];
        CancelRuntimeLoad();
        _runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _runtimeCancellation.Token;
        var generation = _runtimeGeneration;
        var selectionId = _runtimeRootSelectionId;
        var sessionId = _backend.Session?.SessionId;
        bool IsCurrent() => !token.IsCancellationRequested && generation == _runtimeGeneration &&
            CanReadRuntime() && selectionId == SelectedNode?.Id && sessionId == _backend.Session?.SessionId &&
            ReferenceEquals(location, _runtimePath.LastOrDefault());
        RuntimeRows.Clear();
        RuntimeError = "";
        RuntimeIsLoading = true;
        RuntimeTitle = $"{location.TypeName ?? location.Label} · #{location.ObjectId}";
        RuntimeCollectionSupported = location.CollectionKind is RuntimeCollectionKind.List or RuntimeCollectionKind.Dictionary;
        _runtimeSectionIndex = location.Section;
        Raise(nameof(RuntimeSectionIndex));
        RuntimeSummary = $"正在读取对象 #{location.ObjectId}…";
        _runtimeHasMore = false;
        RefreshRuntimeCommands();
        try
        {
            RuntimeObjectSnapshot? members = null;
            if (location.Section == 0 || location.CollectionKind is not (RuntimeCollectionKind.List or RuntimeCollectionKind.Dictionary))
            {
                members = await _client.GetRuntimeMembersAsync(location.ObjectId, location.MemberOffset, RuntimePageSize, token);
                if (!IsCurrent()) return;
                location.TypeName = members.TypeName;
                location.CollectionKind = members.CollectionKind;
            }
            RuntimeTitle = $"{location.TypeName} · #{location.ObjectId}";
            RuntimeCollectionSupported = location.CollectionKind is RuntimeCollectionKind.List or RuntimeCollectionKind.Dictionary;
            if (location.AutoSection)
            {
                location.Section = RuntimeCollectionSupported ? 1 : 0;
                location.AutoSection = false;
            }
            _runtimeSectionIndex = location.Section;
            Raise(nameof(RuntimeSectionIndex));
            var ancestors = _runtimePath.Select(item => item.ObjectId).ToHashSet();
            var rowGeneration = generation;
            bool CanOpenRow() => rowGeneration == _runtimeGeneration && CanReadRuntime();
            if (location.Section == 1)
            {
                if (!RuntimeCollectionSupported)
                {
                    _runtimeTotalCount = 0;
                    RuntimeSummary = location.CollectionKind == RuntimeCollectionKind.UnsupportedEnumerable
                        ? "仅实现 IEnumerable：不枚举延迟或潜在无穷序列。可切回公共成员。"
                        : "该对象不支持 IList / IDictionary 集合读取；这不表示集合为空。可切回公共成员。";
                }
                else
                {
                    var items = await _client.GetRuntimeItemsAsync(location.ObjectId, location.ItemOffset, RuntimePageSize, token);
                    if (!IsCurrent()) return;
                    foreach (var item in items.Items)
                        RuntimeRows.Add(new RuntimeRowViewModel(item, ancestors, OpenRuntimeValueAsync, CanOpenRow));
                    _runtimeTotalCount = items.TotalCount;
                    _runtimeHasMore = items.HasMore;
                    RuntimeSummary = $"{items.Kind} · 本次观察 {_runtimeTotalCount} 项；跨页期间集合可能变化。";
                }
            }
            else
            {
                foreach (var member in members!.Members)
                    RuntimeRows.Add(new RuntimeRowViewModel(member, ancestors, OpenRuntimeValueAsync, CanOpenRow));
                _runtimeTotalCount = members.TotalCount;
                _runtimeHasMore = members.HasMore;
                RuntimeSummary = $"公共成员 / 字段 · {_runtimeTotalCount} 项 · 只读" +
                    (RuntimeCollectionSupported ? " · 可切换集合元素" : location.CollectionKind == RuntimeCollectionKind.UnsupportedEnumerable ? " · IEnumerable 未枚举" : "");
            }
            if (_runtimeHasMore && location.Offset + RuntimePageSize > RuntimeMaximumOffset)
                RuntimeSummary += " · 已到安全分页上限（Offset 10,000），仍可能存在后续项。";
            if (RuntimeRows.Count == 0 && !RuntimeHasError && (location.Section == 0 || RuntimeCollectionSupported))
                RuntimeSummary += location.Offset == 0 ? " · 当前页无项。" : " · 当前偏移已无项，可返回上一页。";
            if (HasBindingNavigationOrigin) RuntimeSummary = BindingNavigationContext + "\n" + RuntimeSummary;
            UpdateRuntimeNavigation();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!IsCurrent()) return;
            _runtimeTotalCount = 0;
            RuntimeError = exception is RpcException rpc
                ? $"[{rpc.Code}] " + (rpc.Code == "object_expired"
                    ? "弱对象 ID 已失效。临时 getter 结果可能已被回收；可后退重读该成员，或返回所选控件后重试。"
                    : rpc.Message)
                : exception.Message;
            RuntimeSummary = "对象读取未完成，不能据此判断值为 null 或集合为空。";
            UpdateRuntimeNavigation();
        }
        finally
        {
            if (IsCurrent())
            {
                RuntimeIsLoading = false;
                RefreshRuntimeCommands();
            }
        }
    }

    private async Task OpenRuntimeValueAsync(long objectId, string label)
    {
        if (!CanReadRuntime()) return;
        var ancestor = _runtimePath.FindIndex(location => location.ObjectId == objectId);
        if (ancestor >= 0)
            _runtimePath.RemoveRange(ancestor + 1, _runtimePath.Count - ancestor - 1);
        else
            _runtimePath.Add(new RuntimeLocation(objectId, label) { AutoSection = true });
        UpdateRuntimeNavigation();
        await RefreshRuntimeAsync();
    }

    private async Task NavigateRuntimeBackAsync(int index)
    {
        if (!CanReadRuntime() || index < 0 || index >= _runtimePath.Count) return;
        _runtimePath.RemoveRange(index + 1, _runtimePath.Count - index - 1);
        UpdateRuntimeNavigation();
        await RefreshRuntimeAsync();
    }

    private async Task ChangeRuntimePageAsync(int delta)
    {
        if (!CanReadRuntime() || _runtimePath.LastOrDefault() is not { } location) return;
        var offset = location.Offset + delta;
        if (offset < 0 || offset > RuntimeMaximumOffset || delta > 0 && !_runtimeHasMore) return;
        if (location.Section == 1) location.ItemOffset = offset;
        else location.MemberOffset = offset;
        await RefreshRuntimeAsync();
    }

    private void UpdateRuntimeNavigation()
    {
        RuntimeBreadcrumbs.Clear();
        for (var i = 0; i < _runtimePath.Count; i++)
        {
            var index = i;
            var generation = _runtimeGeneration;
            var location = _runtimePath[i];
            RuntimeBreadcrumbs.Add(new RuntimeBreadcrumbViewModel($"{location.Label} · #{location.ObjectId}",
                new AsyncCommand(() => NavigateRuntimeBackAsync(index), ReportError,
                    () => generation == _runtimeGeneration && CanReadRuntime())));
        }
        Raise(nameof(RuntimeObjectId));
        Raise(nameof(RuntimePageText));
        RefreshRuntimeCommands();
    }

    private void ResetRuntime()
    {
        ResetBindingNavigation();
        CancelRuntimeLoad();
        _runtimeRootSelectionId = null;
        _runtimePath.Clear();
        RuntimeRows.Clear();
        RuntimeBreadcrumbs.Clear();
        _runtimeHasMore = false;
        _runtimeTotalCount = 0;
        _runtimeSectionIndex = 0;
        Raise(nameof(RuntimeSectionIndex));
        RuntimeCollectionSupported = false;
        RuntimeError = "";
        RuntimeTitle = "选择一个控件";
        RuntimeSummary = "连接 Native 应用并选择控件后，激活此页读取 CLR 对象。";
        Raise(nameof(RuntimeObjectId));
        Raise(nameof(RuntimePageText));
        RefreshRuntimeCommands();
    }

    private sealed class RuntimeLocation(long objectId, string label)
    {
        public long ObjectId { get; } = objectId;
        public string Label { get; } = label;
        public string? TypeName { get; set; }
        public RuntimeCollectionKind? CollectionKind { get; set; }
        public int MemberOffset { get; set; }
        public int ItemOffset { get; set; }
        public int Section { get; set; }
        public bool AutoSection { get; set; }
        public int Offset => Section == 1 ? ItemOffset : MemberOffset;
    }
}

public sealed record RuntimeBreadcrumbViewModel(string Label, AsyncCommand OpenCommand);
