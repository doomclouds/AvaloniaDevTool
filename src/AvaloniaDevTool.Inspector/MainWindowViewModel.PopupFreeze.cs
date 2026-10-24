using System.Collections.ObjectModel;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private PopupFreezeStatusDto? _popupFreeze;
    private PopupFreezeRootOption? _selectedPopupFreezeRoot;
    private DispatcherTimer? _popupFreezeTimer;
    private bool _popupFreezeRefreshing;
    public ObservableCollection<PopupFreezeRootOption> PopupFreezeRoots { get; } = [];
    public AsyncCommand FreezePopupsCommand { get; private set; } = null!;
    public AsyncCommand ResumeSelectedPopupsCommand { get; private set; } = null!;
    public AsyncCommand ResumePopupsCommand { get; private set; } = null!;
    public AsyncCommand RefreshPopupFreezeCommand { get; private set; } = null!;
    public PopupFreezeStatusDto? CurrentPopupFreeze => _popupFreeze;
    public PopupFreezeRootOption? SelectedPopupFreezeRoot
    {
        get => _selectedPopupFreezeRoot;
        set { if (Set(ref _selectedPopupFreezeRoot, value)) RefreshPopupFreezeCommands(); }
    }
    public string PopupFreezeSummary => _popupFreeze is { } state
        ? $"{(state.IsActive ? "弹层保持已开启（部分支持）" : "弹层保持已关闭")} · {state.Roots.Count} 个配置根 · {state.Popups.Count} 个弹层\n" +
          string.Join("\n", state.Popups.Select(popup => $"#{popup.PopupId} {popup.Kind} · {(popup.IsUsingOverlayLayer ? "Overlay" : "Native")} · " +
              (popup.HasOverride ? "已抑制 Popup 外部点击 / 失焦关闭" : popup.IsLightDismissEnabled ? "应用允许 light-dismiss" : "应用原本禁用 light-dismiss") +
              "\n  限制：" + PopupFreezeLimitation(popup))) +
          (state.Conflicts.Count == 0 ? "" : "\n" + string.Join("\n", state.Conflicts))
        : "连接后可按配置根冻结弹层自动关闭。";

    private static string PopupFreezeLimitation(PopupFreezePopupDto popup) => popup.Kind switch
    {
        "ToolTip" => "指针移出、点击和计时关闭仍生效；应用主动关闭优先。",
        "ContextMenu" => "Overlay 外部点击及菜单自身失焦关闭仍生效；菜单项、Escape 和应用 Close 保持正常。",
        _ => "transient Flyout 移出关闭仍生效；目标卸载和应用 Close / Hide 保持正常。"
    };

    private void InitializePopupFreeze()
    {
        FreezePopupsCommand = new AsyncCommand(() => SetSelectedPopupFreezeAsync(true), ReportError,
            () => CanUseNativeTools && SelectedPopupFreezeRoot is not null);
        ResumeSelectedPopupsCommand = new AsyncCommand(() => SetSelectedPopupFreezeAsync(false), ReportError,
            () => CanUseNativeTools && SelectedPopupFreezeRoot is not null);
        ResumePopupsCommand = new AsyncCommand(ResumePopupsAsync, ReportError,
            () => CanUseNativeTools && _popupFreeze?.IsActive == true);
        RefreshPopupFreezeCommand = new AsyncCommand(RefreshPopupFreezeAsync, ReportError, () => CanUseNativeTools);
        _popupFreezeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _popupFreezeTimer.Tick += OnPopupFreezeTick;
        _popupFreezeTimer.Start();
    }

    private void RefreshPopupFreezeCommands()
    {
        FreezePopupsCommand?.Refresh(); ResumeSelectedPopupsCommand?.Refresh();
        ResumePopupsCommand?.Refresh(); RefreshPopupFreezeCommand?.Refresh();
    }

    public async Task RefreshPopupFreezeAsync()
    {
        if (!CanUseNativeTools || _popupFreezeRefreshing) return;
        _popupFreezeRefreshing = true;
        var session = _backend.Session?.SessionId;
        try
        {
            var state = await _client.GetPopupFreezeAsync(_lifetime.Token);
            if (CanUseNativeTools && session == _backend.Session?.SessionId && !_disposed) SetPopupFreeze(state);
        }
        finally { _popupFreezeRefreshing = false; }
    }

    private async Task SetSelectedPopupFreezeAsync(bool frozen)
    {
        var session = _backend.Session?.SessionId;
        var state = await _client.SetPopupFreezeAsync(new(frozen, SelectedPopupFreezeRoot?.Id), _lifetime.Token);
        if (CanUseNativeTools && session == _backend.Session?.SessionId && !_disposed) SetPopupFreeze(state);
    }

    private async Task ResumePopupsAsync()
    {
        var session = _backend.Session?.SessionId;
        var state = await _client.ResumePopupsAsync(_lifetime.Token);
        if (CanUseNativeTools && session == _backend.Session?.SessionId && !_disposed) SetPopupFreeze(state);
    }

    private void SetPopupFreeze(PopupFreezeStatusDto state)
    {
        _popupFreeze = state;
        var selectedId = SelectedPopupFreezeRoot?.Id;
        var options = new[] { new PopupFreezeRootOption(null, "全部配置根（包括后续新增根）") }
            .Concat(state.Roots.Select(root => new PopupFreezeRootOption(root.RootId,
                $"#{root.RootId} {root.Title}{(root.IsFrozen ? " · 保持已开启" : "")}"))).ToArray();
        if (!PopupFreezeRoots.SequenceEqual(options))
        {
            Replace(PopupFreezeRoots, options);
            SelectedPopupFreezeRoot = PopupFreezeRoots.FirstOrDefault(root => root.Id == selectedId) ?? PopupFreezeRoots[0];
        }
        Raise(nameof(CurrentPopupFreeze)); Raise(nameof(PopupFreezeSummary)); RefreshPopupFreezeCommands();
    }

    private void ResetPopupFreeze()
    {
        _popupFreeze = null;
        PopupFreezeRoots.Clear(); SelectedPopupFreezeRoot = null;
        Raise(nameof(CurrentPopupFreeze)); Raise(nameof(PopupFreezeSummary)); RefreshPopupFreezeCommands();
    }

    private void OnPopupFreezeTick(object? sender, EventArgs args)
    {
        if (CanUseNativeTools) _ = RunSafelyAsync(RefreshPopupFreezeAsync);
    }

    private void DisposePopupFreeze()
    {
        if (_popupFreezeTimer is not { } timer) return;
        timer.Stop(); timer.Tick -= OnPopupFreezeTick;
    }
}

public sealed record PopupFreezeRootOption(long? Id, string Title)
{
    public override string ToString() => Title;
}
