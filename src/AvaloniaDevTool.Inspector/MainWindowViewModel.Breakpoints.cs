using System.Collections.ObjectModel;
using System.Globalization;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private readonly HashSet<long> _breakpointHitSequences = [];
    private int _breakpointKindIndex;
    private string _breakpointMemberId = "";
    private string _breakpointCompareTo = "1";
    private string _breakpointObjectId = "";
    private string _breakpointSourceObjectId = "";
    private bool _breakpointSelectedObjectOnly = true;
    private bool _breakpointEnabled = true;
    private bool _breakpointLogMessage = true;
    private bool _breakpointRemoveOnHit;
    private bool _breakpointSuspend;
    private long _breakpointDropped;
    private long _breakpointDefinitionsRevision;
    private long _breakpointRefreshGeneration;
    private bool _replacingBreakpoints;
    private PropertyRowViewModel? _selectedBreakpointMember;
    private BreakpointRowViewModel? _selectedBreakpoint;
    private BreakpointHit? _selectedBreakpointHit;
    private BreakpointComparisonChoice _breakpointComparison = new(BreakpointComparison.Always, "始终");
    public ObservableCollection<PropertyRowViewModel> BreakpointMembers { get; } = [];
    public ObservableCollection<BreakpointRowViewModel> Breakpoints { get; } = [];
    public ObservableCollection<BreakpointHit> BreakpointHits { get; } = [];
    public IReadOnlyList<string> BreakpointKinds { get; } = ["属性变化", "路由事件"];
    public IReadOnlyList<BreakpointComparisonChoice> BreakpointComparisons { get; } =
    [
        new(BreakpointComparison.Always, "始终"), new(BreakpointComparison.LessThan, "<"),
        new(BreakpointComparison.LessThanOrEqual, "≤"), new(BreakpointComparison.Equal, "="),
        new(BreakpointComparison.GreaterThan, ">"), new(BreakpointComparison.GreaterThanOrEqual, "≥")
    ];
    public AsyncCommand CreateBreakpointCommand { get; private set; } = null!;
    public AsyncCommand UpdateBreakpointCommand { get; private set; } = null!;
    public AsyncCommand RemoveBreakpointCommand { get; private set; } = null!;
    public AsyncCommand ResetBreakpointCountCommand { get; private set; } = null!;
    public AsyncCommand UseSelectedBreakpointSourceCommand { get; private set; } = null!;
    public string BreakpointSummary => $"{Breakpoints.Count} / 128 个定义 · {BreakpointHits.Count} / 200 条命中记录 · 丢弃 {_breakpointDropped} 条。条件比较累计命中次数。";
    public string BreakpointMemberId { get => _breakpointMemberId; set => Set(ref _breakpointMemberId, value ?? ""); }
    public string BreakpointCompareTo { get => _breakpointCompareTo; set => Set(ref _breakpointCompareTo, value ?? ""); }
    public string BreakpointObjectId { get => _breakpointObjectId; set => Set(ref _breakpointObjectId, value ?? ""); }
    public string BreakpointSourceObjectId { get => _breakpointSourceObjectId; set => Set(ref _breakpointSourceObjectId, value ?? ""); }
    public bool BreakpointSelectedObjectOnly { get => _breakpointSelectedObjectOnly; set => Set(ref _breakpointSelectedObjectOnly, value); }
    public bool BreakpointEnabled { get => _breakpointEnabled; set => Set(ref _breakpointEnabled, value); }
    public bool BreakpointLogMessage { get => _breakpointLogMessage; set => Set(ref _breakpointLogMessage, value); }
    public bool BreakpointRemoveOnHit { get => _breakpointRemoveOnHit; set => Set(ref _breakpointRemoveOnHit, value); }
    public bool BreakpointSuspend { get => _breakpointSuspend; set => Set(ref _breakpointSuspend, value); }
    public BreakpointComparisonChoice BreakpointComparisonChoice { get => _breakpointComparison; set => Set(ref _breakpointComparison, value); }
    public int BreakpointKindIndex
    {
        get => _breakpointKindIndex;
        set
        {
            if (Set(ref _breakpointKindIndex, value))
            {
                Raise(nameof(IsPropertyBreakpoint));
                Raise(nameof(IsEventBreakpoint));
                BreakpointMemberId = value == 1 ? "Avalonia.Controls.Button:Click" : SelectedBreakpointMember?.Id ?? "";
            }
        }
    }
    public bool IsPropertyBreakpoint => BreakpointKindIndex == 0;
    public bool IsEventBreakpoint => BreakpointKindIndex == 1;
    public PropertyRowViewModel? SelectedBreakpointMember
    {
        get => _selectedBreakpointMember;
        set
        {
            if (Set(ref _selectedBreakpointMember, value) && IsPropertyBreakpoint && value is not null)
                BreakpointMemberId = value.Id;
        }
    }
    public BreakpointRowViewModel? SelectedBreakpoint
    {
        get => _selectedBreakpoint;
        set
        {
            if (!Set(ref _selectedBreakpoint, value)) return;
            if (!_replacingBreakpoints && value is not null) ApplyBreakpointDefinition(value.Info.Definition);
            RefreshBreakpointCommands();
        }
    }

    private void ApplyBreakpointDefinition(BreakpointSpec definition)
    {
        BreakpointKindIndex = definition.Kind == BreakpointKind.Property ? 0 : 1;
        BreakpointMemberId = definition.MemberId;
        BreakpointSelectedObjectOnly = definition.ObjectId is not null;
        BreakpointObjectId = definition.ObjectId?.ToString(CultureInfo.InvariantCulture) ?? "";
        BreakpointSourceObjectId = definition.SourceObjectId?.ToString(CultureInfo.InvariantCulture) ?? "";
        BreakpointEnabled = definition.Enabled;
        BreakpointLogMessage = definition.LogMessage;
        BreakpointRemoveOnHit = definition.RemoveOnHit;
        BreakpointSuspend = definition.Suspend;
        BreakpointCompareTo = definition.CompareTo.ToString(CultureInfo.InvariantCulture);
        BreakpointComparisonChoice = BreakpointComparisons.Single(choice => choice.Value == definition.Comparison);
    }
    public BreakpointHit? SelectedBreakpointHit
    {
        get => _selectedBreakpointHit;
        set
        {
            if (Set(ref _selectedBreakpointHit, value) && value?.ObjectId is { } id && CanUseNativeTools)
            {
                var generation = ++_pickGeneration;
                _ = RunSafelyAsync(() => LocatePickedAsync(id, generation));
            }
        }
    }

    private void InitializeBreakpoints()
    {
        CreateBreakpointCommand = new AsyncCommand(CreateBreakpointAsync, ReportError, () => CanUseNativeTools);
        UpdateBreakpointCommand = new AsyncCommand(UpdateBreakpointAsync, ReportError, () => CanUseNativeTools && SelectedBreakpoint is not null);
        RemoveBreakpointCommand = new AsyncCommand(RemoveBreakpointAsync, ReportError, () => CanUseNativeTools && SelectedBreakpoint is not null);
        ResetBreakpointCountCommand = new AsyncCommand(ResetBreakpointCountAsync, ReportError, () => CanUseNativeTools && SelectedBreakpoint is not null);
        UseSelectedBreakpointSourceCommand = new AsyncCommand(() =>
        {
            if (!HasSelectedNativeElement()) throw new RpcException("select_source", "请先在树中选择事件源对象。");
            BreakpointSourceObjectId = SelectedNode!.Id.ToString(CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        }, ReportError, () => CanUseNativeTools);
        _client.BreakpointHit += HandleBreakpointHit;
        _client.BreakpointsChanged += HandleBreakpointsChanged;
    }

    private void RefreshBreakpointCommands()
    {
        CreateBreakpointCommand?.Refresh();
        UpdateBreakpointCommand?.Refresh();
        RemoveBreakpointCommand?.Refresh();
        ResetBreakpointCountCommand?.Refresh();
        UseSelectedBreakpointSourceCommand?.Refresh();
    }

    private void UpdateBreakpointMembers()
    {
        var selected = SelectedBreakpointMember?.Id;
        BreakpointMembers.Clear();
        foreach (var property in _properties) BreakpointMembers.Add(property);
        var replacement = BreakpointMembers.FirstOrDefault(member => member.Id == selected)
            ?? BreakpointMembers.FirstOrDefault(member => member.Name == "Text" && member.DeclaringType == SelectedNode?.Node.TypeName)
            ?? BreakpointMembers.FirstOrDefault(member => member.Name == "IsEnabled")
            ?? BreakpointMembers.FirstOrDefault();
        SelectedBreakpointMember = replacement;
    }

    private BreakpointSpec ReadBreakpointSpec()
    {
        if (!long.TryParse(BreakpointCompareTo, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
            throw new RpcException("invalid_request", "累计命中次数必须为非负整数。");
        if (string.IsNullOrWhiteSpace(BreakpointMemberId))
            throw new RpcException("invalid_request", "请选择属性，或输入完整 OwnerFullName:Member 标识。");
        long? receiverId = null;
        if (BreakpointSelectedObjectOnly)
        {
            receiverId = ReadBreakpointObjectId(BreakpointObjectId);
            if (receiverId is null && !HasSelectedNativeElement())
                throw new RpcException("select_source", "请先选择断点目标／接收节点，或取消目标限制。");
            receiverId ??= SelectedNode!.Id;
        }
        return new BreakpointSpec(IsPropertyBreakpoint ? BreakpointKind.Property : BreakpointKind.Event,
            BreakpointMemberId.Trim(), receiverId,
            BreakpointEnabled, BreakpointComparisonChoice.Value, count,
            BreakpointLogMessage, BreakpointRemoveOnHit, BreakpointSuspend,
            IsEventBreakpoint ? ReadBreakpointObjectId(BreakpointSourceObjectId) : null);
    }

    private static long? ReadBreakpointObjectId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0) return id;
        throw new RpcException("invalid_request", "对象 ID 必须为正整数，留空表示不限制事件源。");
    }

    private async Task CreateBreakpointAsync()
    {
        var created = await _client.CreateBreakpointAsync(ReadBreakpointSpec(), _lifetime.Token);
        await RefreshBreakpointsAsync();
        SelectedBreakpoint = Breakpoints.FirstOrDefault(row => row.Id == created.Id);
        Status = $"已创建断点 #{created.Id}。";
    }

    private async Task UpdateBreakpointAsync()
    {
        if (SelectedBreakpoint is not { } row) return;
        await _client.UpdateBreakpointAsync(row.Id, ReadBreakpointSpec(), cancellationToken: _lifetime.Token);
        await RefreshBreakpointsAsync();
        Status = $"已更新断点 #{row.Id}。";
    }

    private async Task RemoveBreakpointAsync()
    {
        if (SelectedBreakpoint is not { } row) return;
        await _client.RemoveBreakpointAsync(row.Id, _lifetime.Token);
        await RefreshBreakpointsAsync();
        Status = $"已删除断点 #{row.Id}。";
    }

    private async Task ResetBreakpointCountAsync()
    {
        if (SelectedBreakpoint is not { } row) return;
        await _client.UpdateBreakpointAsync(row.Id, row.Info.Definition, resetHitCount: true, cancellationToken: _lifetime.Token);
        await RefreshBreakpointsAsync();
    }

    private async Task ToggleBreakpointAsync(BreakpointRowViewModel row)
    {
        var enabled = !row.Info.Definition.Enabled;
        await _client.UpdateBreakpointAsync(row.Id, row.Info.Definition with { Enabled = enabled },
            cancellationToken: _lifetime.Token);
        if (SelectedBreakpoint?.Id == row.Id) BreakpointEnabled = enabled;
        await RefreshBreakpointsAsync();
    }

    public async Task RefreshBreakpointsAsync()
    {
        if (!CanUseNativeTools) return;
        var sessionId = _backend.Session?.SessionId;
        var refreshGeneration = Interlocked.Increment(ref _breakpointRefreshGeneration);
        var definitionsRevision = Volatile.Read(ref _breakpointDefinitionsRevision);
        var definitions = await _client.GetBreakpointsAsync(_lifetime.Token);
        var history = await _client.GetBreakpointHistoryAsync(_lifetime.Token);
        if (!CanUseNativeTools || sessionId != _backend.Session?.SessionId || _disposed) return;
        if (refreshGeneration == Volatile.Read(ref _breakpointRefreshGeneration) &&
            definitionsRevision == Volatile.Read(ref _breakpointDefinitionsRevision))
            ReplaceBreakpoints(definitions);
        foreach (var hit in history.Hits) AddBreakpointHit(hit);
        _breakpointDropped = Math.Max(_breakpointDropped, history.DroppedCount);
        Raise(nameof(BreakpointSummary));
    }

    private void ReplaceBreakpoints(IReadOnlyList<BreakpointInfo> items)
    {
        var selectedId = SelectedBreakpoint?.Id;
        var selectedDefinition = SelectedBreakpoint?.Info.Definition;
        var existing = Breakpoints.ToDictionary(row => row.Id);
        var ids = items.Select(item => item.Id).ToHashSet();
        _replacingBreakpoints = true;
        try
        {
            // Keep surviving rows in the list so a refresh cannot cancel an active pointer click.
            for (var index = Breakpoints.Count - 1; index >= 0; index--)
                if (!ids.Contains(Breakpoints[index].Id)) Breakpoints.RemoveAt(index);
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (existing.TryGetValue(item.Id, out var row)) row.Refresh(item);
                else row = new BreakpointRowViewModel(item, ToggleBreakpointAsync, ReportError);
                var currentIndex = Breakpoints.IndexOf(row);
                if (currentIndex < 0) Breakpoints.Insert(index, row);
                else if (currentIndex != index) Breakpoints.Move(currentIndex, index);
            }
            SelectedBreakpoint = Breakpoints.FirstOrDefault(row => row.Id == selectedId);
        }
        finally { _replacingBreakpoints = false; }
        // Navigating to a hit refreshes the same definitions; keep an in-progress edit intact.
        if (SelectedBreakpoint is { } selected && selected.Info.Definition != selectedDefinition)
            ApplyBreakpointDefinition(selected.Info.Definition);
        Raise(nameof(BreakpointSummary));
    }

    private void HandleBreakpointHit(BreakpointHit hit)
    {
        var sessionId = _backend.Session?.SessionId;
        Dispatch(() =>
        {
            if (!CanUseNativeTools || sessionId != _backend.Session?.SessionId) return;
            AddBreakpointHit(hit);
            var row = Breakpoints.FirstOrDefault(item => item.Id == hit.BreakpointId);
            if (row is not null) row.Refresh(row.Info with { HitCount = hit.HitCount });
        });
    }

    private void AddBreakpointHit(BreakpointHit hit)
    {
        if (_breakpointHitSequences.Contains(hit.Sequence) ||
            (BreakpointHits.Count >= 200 && hit.Sequence < BreakpointHits[0].Sequence)) return;
        var index = 0;
        while (index < BreakpointHits.Count && BreakpointHits[index].Sequence < hit.Sequence) index++;
        _breakpointHitSequences.Add(hit.Sequence);
        BreakpointHits.Insert(index, hit);
        while (BreakpointHits.Count > 200)
        {
            _breakpointHitSequences.Remove(BreakpointHits[0].Sequence);
            BreakpointHits.RemoveAt(0);
            _breakpointDropped++;
        }
        Raise(nameof(BreakpointSummary));
    }

    private void HandleBreakpointsChanged(BreakpointChangedNotification notification)
    {
        var sessionId = _backend.Session?.SessionId;
        var revision = Interlocked.Increment(ref _breakpointDefinitionsRevision);
        Dispatch(() =>
        {
            if (CanUseNativeTools && sessionId == _backend.Session?.SessionId &&
                revision == Volatile.Read(ref _breakpointDefinitionsRevision))
                ReplaceBreakpoints(notification.Breakpoints);
        });
    }

    private void ResetBreakpoints()
    {
        Interlocked.Increment(ref _breakpointDefinitionsRevision);
        Interlocked.Increment(ref _breakpointRefreshGeneration);
        BreakpointMembers.Clear();
        Breakpoints.Clear();
        BreakpointHits.Clear();
        _breakpointHitSequences.Clear();
        _breakpointDropped = 0;
        BreakpointObjectId = "";
        BreakpointSourceObjectId = "";
        SelectedBreakpoint = null;
        _selectedBreakpointMember = null;
        _selectedBreakpointHit = null;
        Raise(nameof(SelectedBreakpointMember));
        Raise(nameof(SelectedBreakpointHit));
        Raise(nameof(BreakpointSummary));
        RefreshBreakpointCommands();
    }

    private void DisposeBreakpoints()
    {
        _client.BreakpointHit -= HandleBreakpointHit;
        _client.BreakpointsChanged -= HandleBreakpointsChanged;
    }
}

public sealed record BreakpointComparisonChoice(BreakpointComparison Value, string Label);

public sealed class BreakpointRowViewModel : ObservableObject
{
    private BreakpointInfo _info;
    public BreakpointRowViewModel(BreakpointInfo info, Func<BreakpointRowViewModel, Task> toggle, Action<Exception> onError)
    {
        _info = info;
        ToggleCommand = new AsyncCommand(() => toggle(this), onError);
    }
    public BreakpointInfo Info => _info;
    public long Id => _info.Id;
    public string MemberId => _info.Definition.MemberId;
    public string Scope => _info.Definition.Kind == BreakpointKind.Event
        ? $"源 {(_info.Definition.SourceObjectId is { } source ? $"#{source}" : "全局")} · 接收节点 {(_info.Definition.ObjectId is { } receiver ? $"#{receiver}" : "全局")}"
        : _info.Definition.ObjectId is { } id ? $"对象 #{id}" : "全局";
    public string State => _info.State.ToString();
    public long HitCount => _info.HitCount;
    public string ToggleText => _info.Definition.Enabled ? "禁用" : "启用";
    public AsyncCommand ToggleCommand { get; }
    public void Refresh(BreakpointInfo info)
    {
        _info = info;
        Raise(nameof(Info));
        Raise(nameof(MemberId));
        Raise(nameof(Scope));
        Raise(nameof(State));
        Raise(nameof(HitCount));
        Raise(nameof(ToggleText));
    }
}

