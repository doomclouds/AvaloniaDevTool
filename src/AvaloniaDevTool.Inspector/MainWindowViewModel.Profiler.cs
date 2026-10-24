using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private DispatcherTimer? _profilerTimer;
    private CancellationTokenSource? _profilerLoad;
    private ProfilerStatusDto? _profilerStatus;
    private long _profilerGeneration;
    private bool _profilerBusy;
    private bool _profilerRefreshing;
    private bool _profileStyleAttach = true;
    private bool _profileStyleEvaluate = true;
    private bool _profileResources = true;
    private string _profilerFilter = "";
    private ProfilerGroupRowViewModel? _selectedProfilerGroup;

    public ObservableCollection<ProfilerGroupRowViewModel> ProfilerGroups { get; } = [];
    public ObservableCollection<ProfilerSampleRowViewModel> ProfilerSamples { get; } = [];
    public AsyncCommand StartProfilerCommand { get; private set; } = null!;
    public AsyncCommand StopProfilerCommand { get; private set; } = null!;
    public AsyncCommand ClearProfilerCommand { get; private set; } = null!;
    public AsyncCommand RefreshProfilerCommand { get; private set; } = null!;
    public bool ProfileStyleAttach { get => _profileStyleAttach; set => Set(ref _profileStyleAttach, value); }
    public bool ProfileStyleEvaluate { get => _profileStyleEvaluate; set => Set(ref _profileStyleEvaluate, value); }
    public bool ProfileResources { get => _profileResources; set => Set(ref _profileResources, value); }
    public string ProfilerFilter { get => _profilerFilter; set => Set(ref _profilerFilter, value ?? ""); }
    public ProfilerStatusDto? CurrentProfilerStatus => _profilerStatus;
    public ProfilerGroupRowViewModel? SelectedProfilerGroup
    {
        get => _selectedProfilerGroup;
        set { if (Set(ref _selectedProfilerGroup, value)) Raise(nameof(ProfilerGroupDetails)); }
    }
    public string ProfilerSummary => _profilerStatus is { } state
        ? $"{(state.IsRunning ? "正在采集" : "已停止")} · {state.GroupCount} 个分组 · 保留 {state.BufferedSampleCount}/{state.Capacity} 条 · 进行中 {state.InFlightCount}\n" +
          $"完成 {state.CompletedOperations} · 筛除 {state.FilteredOperations} · 历史淘汰 {state.EvictedHistorySamples} · 通知丢弃 {state.DroppedPendingSamples} · 分组限制 {state.DroppedGroupSamples} · 并发限制 {state.DroppedInFlightOperations} · 未完成 {state.IncompleteOperations} · 回调错误 {state.CallbackErrors} · 通知错误 {state.NotificationErrors}" +
          (string.IsNullOrWhiteSpace(state.LastError) ? "" : "\n" + state.LastError)
        : "选择采集类型并开始，再在目标应用中操作。筛选文本在开始时生效；耗时单位为微秒。";
    public string ProfilerGroupDetails => SelectedProfilerGroup?.Details ?? "选择分组查看计数含义。成功、未匹配和未知分别统计；缺失的资源 Result 标签保持未知。";

    private void InitializeProfiler()
    {
        StartProfilerCommand = new AsyncCommand(StartProfilerCaptureAsync, ReportError,
            () => CanUseNativeTools && !_profilerBusy && _profilerStatus?.IsRunning != true);
        StopProfilerCommand = new AsyncCommand(() => ChangeProfilerAsync(
            () => _client.StopProfilerAsync(_lifetime.Token)), ReportError,
            () => CanUseNativeTools && !_profilerBusy && _profilerStatus?.IsRunning == true);
        ClearProfilerCommand = new AsyncCommand(() => ChangeProfilerAsync(
            () => _client.ClearProfilerAsync(_lifetime.Token)), ReportError,
            () => CanUseNativeTools && !_profilerBusy);
        RefreshProfilerCommand = new AsyncCommand(RefreshProfilerAsync, ReportError,
            () => CanUseNativeTools && !_profilerBusy);
        _client.ProfilerSamplesAdded += HandleProfilerSamplesAdded;
        _profilerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _profilerTimer.Tick += OnProfilerTick;
        _profilerTimer.Start();
    }

    private async Task StartProfilerCaptureAsync()
    {
        List<ProfilerCategory> categories = [];
        if (ProfileStyleAttach) categories.Add(ProfilerCategory.StyleAttach);
        if (ProfileStyleEvaluate) categories.Add(ProfilerCategory.StyleEvaluate);
        if (ProfileResources) categories.Add(ProfilerCategory.ResourceLookup);
        if (categories.Count == 0) { Status = "至少选择一种性能活动。"; return; }
        await ChangeProfilerAsync(() => _client.StartProfilerAsync(
            new ProfilerStartRequest(categories, ProfilerFilter), _lifetime.Token));
    }

    private async Task ChangeProfilerAsync(Func<Task<ProfilerStatusDto>> change)
    {
        CancelProfilerLoad();
        var generation = _profilerGeneration;
        var sessionId = _backend.Session?.SessionId;
        _profilerBusy = true;
        RefreshProfilerCommands();
        try
        {
            var status = await change();
            if (!ProfilerResponseIsCurrent(generation, sessionId)) return;
            SetProfilerStatus(status);
            ProfilerGroups.Clear();
            ProfilerSamples.Clear();
            SelectedProfilerGroup = null;
        }
        finally
        {
            if (generation == _profilerGeneration) _profilerBusy = false;
            RefreshProfilerCommands();
        }
        if (ProfilerResponseIsCurrent(generation, sessionId)) await RefreshProfilerAsync();
    }

    public async Task RefreshProfilerAsync()
    {
        if (!CanUseNativeTools || _profilerBusy || _profilerRefreshing) return;
        _profilerLoad?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _profilerLoad = cancellation;
        var generation = _profilerGeneration;
        var sessionId = _backend.Session?.SessionId;
        _profilerRefreshing = true;
        try
        {
            var snapshot = await _client.GetProfilerSnapshotAsync(cancellation.Token);
            if (!ProfilerResponseIsCurrent(generation, sessionId) || cancellation.IsCancellationRequested) return;
            var selected = SelectedProfilerGroup?.Id;
            SetProfilerStatus(snapshot.Status);
            Replace(ProfilerGroups, snapshot.Groups.Select(item => new ProfilerGroupRowViewModel(item)));
            Replace(ProfilerSamples, snapshot.Samples.Select(item => new ProfilerSampleRowViewModel(item)));
            SelectedProfilerGroup = ProfilerGroups.FirstOrDefault(item => item.Id == selected) ?? ProfilerGroups.FirstOrDefault();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_profilerLoad, cancellation)) _profilerRefreshing = false;
        }
    }

    private bool ProfilerResponseIsCurrent(long generation, string? sessionId) =>
        !_disposed && CanUseNativeTools && generation == _profilerGeneration && sessionId == _backend.Session?.SessionId;

    private void HandleProfilerSamplesAdded(ProfilerSamplesAddedNotification notification)
    {
        var sessionId = _backend.Session?.SessionId;
        Dispatch(() =>
        {
            if (!CanUseNativeTools || _disposed || sessionId != _backend.Session?.SessionId || _profilerBusy ||
                _profilerStatus is not { IsRunning: true } current || current.CaptureId != notification.Status.CaptureId) return;
            if (InspectionTabIndex == 10) _ = RunSafelyAsync(RefreshProfilerAsync);
        });
    }

    private void OnProfilerTick(object? sender, EventArgs args)
    {
        if (InspectionTabIndex == 10 && CanUseNativeTools && _profilerStatus?.IsRunning == true)
            _ = RunSafelyAsync(RefreshProfilerAsync);
    }

    private void SetProfilerStatus(ProfilerStatusDto status)
    {
        _profilerStatus = status;
        Raise(nameof(CurrentProfilerStatus));
        Raise(nameof(ProfilerSummary));
        RefreshProfilerCommands();
    }

    private void RefreshProfilerCommands()
    {
        StartProfilerCommand?.Refresh();
        StopProfilerCommand?.Refresh();
        ClearProfilerCommand?.Refresh();
        RefreshProfilerCommand?.Refresh();
    }

    private void CancelProfilerLoad()
    {
        _profilerGeneration++;
        _profilerLoad?.Cancel();
        _profilerLoad?.Dispose();
        _profilerLoad = null;
        _profilerRefreshing = false;
    }

    private void ResetProfiler()
    {
        CancelProfilerLoad();
        _profilerBusy = false;
        _profilerStatus = null;
        ProfilerGroups.Clear();
        ProfilerSamples.Clear();
        SelectedProfilerGroup = null;
        Raise(nameof(CurrentProfilerStatus));
        Raise(nameof(ProfilerSummary));
        RefreshProfilerCommands();
    }

    private void DisposeProfiler()
    {
        CancelProfilerLoad();
        _client.ProfilerSamplesAdded -= HandleProfilerSamplesAdded;
        if (_profilerTimer is { } timer) { timer.Stop(); timer.Tick -= OnProfilerTick; }
    }
}

public sealed class ProfilerGroupRowViewModel(ProfilerGroupDto group)
{
    public long Id => group.Id;
    public string Name => group.Name;
    public string Category => ProfilerSampleRowViewModel.CategoryText(group.Category);
    public string Summary => $"次数 {group.WindowCount} · 总计 {Format(group.TotalMicroseconds)} μs · 平均 {Format(group.MeanMicroseconds)} μs · 最小 {Format(group.MinMicroseconds)} μs · 最大 {Format(group.MaxMicroseconds)} μs";
    public string Outcomes => $"成功/活动 {group.MatchCount} · 未匹配/不活动 {group.NoMatchCount} · 未知 {group.UnknownCount} · 类型快速拒绝 {group.FastRejectCount}";
    public string Details => $"{Category} · {Name}\n{Summary}\n{Outcomes}\n{group.WindowScope}：仅覆盖当前保留样本。样式匹配包含类型快速拒绝；激活器统计 IsActive；资源查找只把显式 Result=true 记为成功，缺失标签保持未知。";
    private static string Format(double value) => value.ToString("G6", CultureInfo.InvariantCulture);
}

public sealed class ProfilerSampleRowViewModel(ProfilerSampleDto sample)
{
    public long Sequence => sample.Sequence;
    public string Time => sample.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public string Category => CategoryText(sample.Category);
    public string Name => sample.Selector ?? sample.Key ?? sample.Style ?? sample.Operation;
    public string Duration => sample.DurationMicroseconds.ToString("G6", CultureInfo.InvariantCulture) + " μs";
    public string Result => sample.Result ?? (sample.IsActive is { } active ? $"IsActive={active}" : "未知");
    public string Tags => string.Join(" · ", sample.Tags.Select(tag => $"{tag.Key}={tag.Value ?? "null"}"));
    internal static string CategoryText(ProfilerCategory category) => category switch
    {
        ProfilerCategory.StyleAttach => "样式匹配",
        ProfilerCategory.StyleEvaluate => "激活器求值",
        _ => "资源查找"
    };
}
