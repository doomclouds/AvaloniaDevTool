using System.Collections.ObjectModel;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private bool _bindingNavigationChangingPage;
    private BindingNavigationOrigin? _bindingNavigationOrigin;
    private string? _bindingResourceKey;
    private string _bindingResourceSummary = "";
    private readonly List<AsyncCommand> _bindingNavigationCommands = [];
    private bool _activeStyleFramesOnly;
    private bool _runningBindingsOnly;
    private bool _errorBindingsOnly;

    public AsyncCommand ReturnToBindingCommand { get; private set; } = null!;
    public string BindingNavigationContext => _bindingNavigationOrigin is { } origin
        ? $"来自控件 #{origin.SelectionId} · {origin.PropertyName} · {origin.Field}" : "";
    public bool HasBindingNavigationOrigin => IsBindingOriginCurrent();
    public ObservableCollection<BindingResourceMatchViewModel> BindingResourceMatches { get; } = [];
    public string BindingResourceSummary { get => _bindingResourceSummary; private set => Set(ref _bindingResourceSummary, value); }
    public bool HasBindingResourceQuery => IsBindingOriginCurrent() && _bindingResourceKey is not null;
    public IEnumerable<StyleFrameInfo> FilteredStyleFrames => StyleFrames.Where(frame => !ActiveStyleFramesOnly || frame.IsActive);
    public IEnumerable<BindingDiagnosticInfo> FilteredBindingDiagnostics => BindingDiagnostics.Where(binding =>
        (!RunningBindingsOnly || binding.IsRunning == true) &&
        (!ErrorBindingsOnly || !string.IsNullOrWhiteSpace(binding.ErrorType) && binding.ErrorType is not ("None" or "Unknown")));
    public bool ActiveStyleFramesOnly { get => _activeStyleFramesOnly; set { if (Set(ref _activeStyleFramesOnly, value)) Raise(nameof(FilteredStyleFrames)); } }
    public bool RunningBindingsOnly { get => _runningBindingsOnly; set { if (Set(ref _runningBindingsOnly, value)) Raise(nameof(FilteredBindingDiagnostics)); } }
    public bool ErrorBindingsOnly { get => _errorBindingsOnly; set { if (Set(ref _errorBindingsOnly, value)) Raise(nameof(FilteredBindingDiagnostics)); } }

    private void InitializeBindingNavigation()
    {
        ReturnToBindingCommand = new AsyncCommand(ReturnToBindingAsync, ReportError, IsBindingOriginCurrent);
        StyleFrames.CollectionChanged += (_, _) => Raise(nameof(FilteredStyleFrames));
        BindingDiagnostics.CollectionChanged += (_, change) =>
        {
            Raise(nameof(FilteredBindingDiagnostics));
            RefreshBindingNavigationCommands();
            if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                _bindingNavigationCommands.Clear();
        };
    }

    public BindingDiagnosticCardViewModel CreateBindingDiagnosticCard(BindingDiagnosticInfo binding)
    {
        var selectionId = SelectedNode?.Id;
        var sessionId = _backend.Session?.SessionId;
        bool Current() => CanUseNativeTools && !_disposed && InspectionTabIndex == 1 &&
            selectionId == SelectedNode?.Id && sessionId == _backend.Session?.SessionId &&
            BindingDiagnostics.Any(item => ReferenceEquals(item, binding));
        return new(binding, (field, value) =>
        {
            var stringKey = field == "ResourceKey" && value.State == BindingDetailState.Known &&
                value.TypeName == typeof(string).FullName && value.Value is not null && value.Value.Length <= 256;
            if (!stringKey && (value.ObjectId is not > 0 || value.State is not (BindingDetailState.Known or BindingDetailState.Unavailable))) return null;
            var command = new AsyncCommand(async () =>
            {
                if (!Current() || selectionId is null || sessionId is null) return;
                _bindingNavigationOrigin = new(selectionId.Value, sessionId, binding.PropertyName, field);
                _bindingResourceKey = stringKey ? value.Value : null;
                BindingResourceMatches.Clear();
                BindingResourceSummary = "";
                RaiseBindingNavigation();
                if (stringKey)
                {
                    _resourceKeyFilter = value.Value!;
                    _resourceTypeFilter = "";
                    _includeNestedResources = true;
                    Raise(nameof(ResourceKeyFilter));
                    Raise(nameof(ResourceTypeFilter));
                    Raise(nameof(IncludeNestedResources));
                    await ActivateBindingDestinationAsync(2);
                }
                else
                {
                    SetRuntimeBindingRoot(value.ObjectId!.Value, $"{binding.PropertyName} · {field}");
                    await ActivateBindingDestinationAsync(9);
                }
            }, ReportError, Current);
            _bindingNavigationCommands.Add(command);
            return command;
        }, IsBindingOriginCurrent() && _bindingNavigationOrigin?.PropertyName == binding.PropertyName);
    }

    private async Task ActivateBindingDestinationAsync(int index)
    {
        _bindingNavigationChangingPage = true;
        try { InspectionTabIndex = index; }
        finally { _bindingNavigationChangingPage = false; }
        await RefreshActiveInspectionAsync();
    }

    private bool IsBindingOriginCurrent() => CanUseNativeTools && !_disposed && _bindingNavigationOrigin is { } origin &&
        origin.SelectionId == SelectedNode?.Id && origin.SessionId == _backend.Session?.SessionId;

    private async Task ReturnToBindingAsync()
    {
        if (!IsBindingOriginCurrent()) return;
        var context = BindingNavigationContext;
        await ActivateBindingDestinationAsync(1);
        if (IsBindingOriginCurrent()) Status = $"已返回绑定：{context}。控件树选择保持原位。";
    }

    private void BindingNavigationSelectionChanged()
    {
        if (_bindingNavigationOrigin is not null && !IsBindingOriginCurrent()) ResetBindingNavigation();
        RefreshBindingNavigationCommands();
    }

    private void ResetBindingNavigation()
    {
        _bindingNavigationOrigin = null;
        _bindingResourceKey = null;
        BindingResourceMatches.Clear();
        BindingResourceSummary = "";
        RaiseBindingNavigation();
    }

    private void RaiseBindingNavigation()
    {
        Raise(nameof(BindingNavigationContext));
        Raise(nameof(HasBindingNavigationOrigin));
        Raise(nameof(HasBindingResourceQuery));
        RefreshBindingNavigationCommands();
    }

    private void RefreshBindingNavigationCommands()
    {
        ReturnToBindingCommand?.Refresh();
        foreach (var command in _bindingNavigationCommands) command.Refresh();
        // Commands retain only DTOs and weak IDs, but old cards need not accumulate for the session.
        if (_bindingNavigationCommands.Count > 2048) _bindingNavigationCommands.Clear();
    }

    private async Task RefreshBindingResourceMatchesAsync(int generation, CancellationToken token)
    {
        if (!HasBindingResourceQuery || _bindingResourceKey is not { } key) return;
        var origin = _bindingNavigationOrigin;
        BindingResourceMatches.Clear();
        BindingResourceSummary = "正在按真实字符串键检查应用及所选控件的资源作用域…";
        var visited = new HashSet<(long, long, string)>();
        var matches = new List<BindingResourceMatchViewModel>();
        var truncated = false;
        foreach (var root in ResourceDictionaries.Take(32))
        {
            if (!IsCurrentInspection(generation, token) || !ReferenceEquals(origin, _bindingNavigationOrigin) || InspectionTabIndex != 2) return;
            if (_resourceProviders.TryGetValue(root.Id, out var provider) && !provider.CanEnumerate) continue;
            var offset = 0;
            for (var pageIndex = 0; pageIndex < 5; pageIndex++)
            {
                var page = await _client.QueryResourceEntriesAsync(new(root.Id, true, key, Offset: offset, Limit: 50), token);
                if (!IsCurrentInspection(generation, token) || !ReferenceEquals(origin, _bindingNavigationOrigin) || InspectionTabIndex != 2) return;
                truncated |= page.Truncated;
                foreach (var entry in page.Entries.Where(entry => entry.StringKey == key))
                {
                    if (!visited.Add((entry.OwnerDictionaryId, entry.KeyObjectId, entry.ProviderPath))) continue;
                    var captured = entry;
                    var command = new AsyncCommand(() =>
                    {
                        if (!IsBindingOriginCurrent() || InspectionTabIndex != 2 ||
                            !BindingResourceMatches.Any(match => ReferenceEquals(match.Entry, captured))) return Task.CompletedTask;
                        SelectedResourceDictionary = FlattenResources(ResourceDictionaries).FirstOrDefault(item => item.Id == captured.OwnerDictionaryId);
                        return Task.CompletedTask;
                    }, ReportError, () => IsBindingOriginCurrent() && InspectionTabIndex == 2 && ReferenceEquals(origin, _bindingNavigationOrigin) &&
                        BindingResourceMatches.Any(match => ReferenceEquals(match.Entry, captured)));
                    matches.Add(new(entry, command));
                    if (matches.Count >= 200) { truncated = true; break; }
                }
                if (matches.Count >= 200) break;
                if (!page.HasMore) break;
                offset += page.Entries.Count;
                if (pageIndex == 4) truncated = true;
            }
            if (matches.Count >= 200) break;
        }
        truncated |= ResourceDictionaries.Count > 32;
        Replace(BindingResourceMatches, matches);
        BindingResourceSummary = $"真实键“{key}” · {BindingResourceMatches.Count} 个可枚举来源。每项保留所属字典、作用域和主题；这些是候选来源，不推断最终胜出者。" +
            (truncated ? " 查询达到预算，结果不完整。" : "") +
            " 自定义不可枚举提供者及框架选择过程仍可能存在缺口。";
    }

    private sealed record BindingNavigationOrigin(long SelectionId, string SessionId, string PropertyName, string Field);
}

public sealed record BindingResourceMatchViewModel(ResourceQueryEntryInfo Entry, AsyncCommand ShowOwnerCommand)
{
    public string Description => $"{Entry.ProviderPath} · {Entry.Scope}" + (Entry.Theme is { } theme ? $" · {theme}" : "") + $" · 字典 #{Entry.OwnerDictionaryId}";
    public string Value => Entry.Value ?? "null";
}
