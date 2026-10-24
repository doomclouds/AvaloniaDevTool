using System.Collections.ObjectModel;
using Avalonia.Threading;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private DispatcherTimer? _protocolTimer;
    private RpcTraceSnapshot? _protocolSnapshot;
    private RpcTraceEntry? _selectedProtocolTrace;
    private string _protocolFilter = "";
    private bool _protocolPayloads, _protocolErrorsOnly;
    public ObservableCollection<RpcTraceEntry> ProtocolTraceEntries { get; } = [];
    public AsyncCommand StartProtocolTraceCommand { get; private set; } = null!;
    public AsyncCommand StopProtocolTraceCommand { get; private set; } = null!;
    public AsyncCommand ClearProtocolTraceCommand { get; private set; } = null!;
    public AsyncCommand RefreshProtocolTraceCommand { get; private set; } = null!;
    public bool IsProtocolRecording => _protocolSnapshot?.IsRecording == true;
    public bool CanConfigureProtocolCapture => !IsProtocolRecording;
    public bool ProtocolCapturePayloads { get => _protocolPayloads; set => Set(ref _protocolPayloads, value); }
    public bool ProtocolErrorsOnly { get => _protocolErrorsOnly; set { if (Set(ref _protocolErrorsOnly, value)) FilterProtocolTrace(); } }
    public string ProtocolFilter { get => _protocolFilter; set { if (Set(ref _protocolFilter, value ?? "")) FilterProtocolTrace(); } }
    public RpcTraceEntry? SelectedProtocolTrace
    {
        get => _selectedProtocolTrace;
        set { if (Set(ref _selectedProtocolTrace, value)) { Raise(nameof(ProtocolRequestText)); Raise(nameof(ProtocolResponseText)); Raise(nameof(ProtocolTraceDetail)); } }
    }
    public string ProtocolRequestText => SelectedProtocolTrace?.RequestJson ?? "未录制正文。开始录制前可开启正文；默认仅保留方法、关联 ID、字节数、耗时及结果。";
    public string ProtocolResponseText => SelectedProtocolTrace?.ResponseJson ?? "没有已录制的响应正文。通知不含响应，开始前的请求或清空后的迟到响应可能无法关联。";
    public string ProtocolTraceDetail => SelectedProtocolTrace is { } entry
        ? $"连接 {entry.ConnectionId} · 请求 {entry.RequestId?.ToString() ?? "—"} · {entry.Direction} · {entry.State}\n" +
          $"{entry.StartedAt:O} · {entry.DurationMs?.ToString("F3") ?? "—"} ms · 发出 {entry.SentBytes:N0} / 收到 {entry.ReceivedBytes:N0} 字节\n" +
          $"{entry.ErrorCode} {entry.ErrorMessage}" + (entry.PayloadTruncated ? "\n正文已按节点、深度或字节预算截断。" : "")
        : "选择一条请求、响应或通知查看详情。";
    public string ProtocolTraceSummary => _protocolSnapshot is { } snapshot
        ? $"{(snapshot.IsRecording ? "正在录制" : "已停止")} · 显示 {ProtocolTraceEntries.Count} / 保留 {snapshot.Entries.Count} / 上限 {snapshot.Capacity} 条 · " +
          $"{snapshot.BufferedBytes:N0}/{snapshot.ByteBudget:N0} 字节 · 淘汰 {snapshot.EvictedCount:N0} 条 · {(snapshot.IncludePayloads ? "包含有界正文" : "仅元数据")}"
        : "尚未录制。可以先开始录制，再连接目标以包含会话握手。";

    private void InitializeProtocolMonitor()
    {
        _protocolTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _protocolTimer.Tick += OnProtocolTraceTick;
        StartProtocolTraceCommand = new AsyncCommand(() =>
        {
            _client.ProtocolTrace.Start(ProtocolCapturePayloads); RefreshProtocolTrace(); _protocolTimer.Start();
            return Task.CompletedTask;
        }, ReportError, () => IsNativeBackend && !IsProtocolRecording);
        StopProtocolTraceCommand = new AsyncCommand(() =>
        {
            _client.ProtocolTrace.Stop(); _protocolTimer.Stop(); RefreshProtocolTrace(); return Task.CompletedTask;
        }, ReportError, () => IsProtocolRecording);
        ClearProtocolTraceCommand = new AsyncCommand(() =>
        {
            _client.ProtocolTrace.Clear(); RefreshProtocolTrace(); return Task.CompletedTask;
        }, ReportError, () => IsNativeBackend);
        RefreshProtocolTraceCommand = new AsyncCommand(() => { RefreshProtocolTrace(); return Task.CompletedTask; }, ReportError, () => IsNativeBackend);
    }
    private void OnProtocolTraceTick(object? sender, EventArgs args) => RefreshProtocolTrace();
    public void RefreshProtocolTrace()
    {
        if (_disposed) return;
        var snapshot = _client.ProtocolTrace.Snapshot();
        if (_protocolSnapshot?.Revision != snapshot.Revision || _protocolSnapshot.IsRecording != snapshot.IsRecording)
        {
            _protocolSnapshot = snapshot; FilterProtocolTrace();
            Raise(nameof(IsProtocolRecording)); Raise(nameof(CanConfigureProtocolCapture));
        }
        RefreshProtocolTraceCommands();
    }
    private void FilterProtocolTrace()
    {
        var selected = SelectedProtocolTrace?.Sequence;
        var entries = (_protocolSnapshot?.Entries ?? []).Where(entry =>
            (!ProtocolErrorsOnly || entry.ErrorCode is not null || entry.State is "Failed" or "Disconnected" or "CancelledOrFailed") &&
            (string.IsNullOrWhiteSpace(ProtocolFilter) || entry.Method.Contains(ProtocolFilter, StringComparison.OrdinalIgnoreCase) ||
             entry.State.Contains(ProtocolFilter, StringComparison.OrdinalIgnoreCase) || entry.ErrorCode?.Contains(ProtocolFilter, StringComparison.OrdinalIgnoreCase) == true)).ToArray();
        for (var i = 0; i < entries.Length; i++)
            if (i >= ProtocolTraceEntries.Count) ProtocolTraceEntries.Add(entries[i]);
            else if (ProtocolTraceEntries[i] != entries[i]) ProtocolTraceEntries[i] = entries[i];
        while (ProtocolTraceEntries.Count > entries.Length) ProtocolTraceEntries.RemoveAt(ProtocolTraceEntries.Count - 1);
        SelectedProtocolTrace = selected is { } sequence ? entries.FirstOrDefault(entry => entry.Sequence == sequence) : null;
        Raise(nameof(ProtocolTraceSummary));
    }
    private void RefreshProtocolTraceCommands()
    {
        StartProtocolTraceCommand?.Refresh(); StopProtocolTraceCommand?.Refresh();
        ClearProtocolTraceCommand?.Refresh(); RefreshProtocolTraceCommand?.Refresh();
    }
    private void DisposeProtocolMonitor()
    {
        _client.ProtocolTrace.Stop();
        if (_protocolTimer is { } timer) { timer.Stop(); timer.Tick -= OnProtocolTraceTick; }
    }
}
