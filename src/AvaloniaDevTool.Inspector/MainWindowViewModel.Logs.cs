using System.Text;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private string _logCategoryFilter = "";
    private string _logMessageFilter = "";
    private string _logParameterNameFilter = "";
    private string _logParameterValueFilter = "";
    public string LogCategoryFilter { get => _logCategoryFilter; set => Set(ref _logCategoryFilter, value ?? ""); }
    public string LogMessageFilter { get => _logMessageFilter; set => Set(ref _logMessageFilter, value ?? ""); }
    public string LogParameterNameFilter { get => _logParameterNameFilter; set => Set(ref _logParameterNameFilter, value ?? ""); }
    public string LogParameterValueFilter { get => _logParameterValueFilter; set => Set(ref _logParameterValueFilter, value ?? ""); }
    public string SelectedLogTemplate => SelectedActivityEntry?.LogDetails?.MessageTemplate ?? "原始模板不可用";
    public IReadOnlyList<LogParameterInfo> SelectedLogParameters => SelectedActivityEntry?.LogDetails?.Parameters ?? [];
    public string SelectedLogMetadata => SelectedActivityEntry?.LogDetails is { } details
        ? $"{details.Origin} · 参数 {details.ParametersState}" +
          (details.ParametersTruncated ? "（参数已截断）" : "") +
          (details.TemplateTruncated ? " · 模板已截断" : "") +
          (details.ByteBudgetTruncated ? " · 内容按字节预算截断" : "") +
          (details.EventId is { } id ? $" · EventId={id} {details.EventName}" : "") +
          (details.SourceType is { } source ? $" · {source}" : "")
        : "选择日志以查看原始模板、类型参数、异常和作用域。";
    public string SelectedLogException => FormatLogException(SelectedActivityEntry?.LogDetails?.Exception);
    public string SelectedLogScopes => SelectedActivityEntry?.LogDetails?.Scopes is { Count: > 0 } scopes
        ? string.Join(Environment.NewLine, scopes.Select((scope, index) => $"作用域 {index + 1}: " +
            string.Join(", ", scope.Parameters.Select(value => $"{value.Name} ({value.TypeName}) = {LogValue(value)}")) +
            (scope.IsTruncated ? " [已截断]" : "")))
        : "无已捕获的作用域";
    public AsyncCommand JumpToLogSourceCommand { get; private set; } = null!;

    private LogFilter CurrentLogFilter() => new(LogCategoryFilter, LogMessageFilter, LogParameterNameFilter, LogParameterValueFilter);

    private void ApplyActivitySnapshot(IReadOnlyList<ActivityEntry> history, ActivityStatus status)
    {
        // Selection navigation refreshes this page. Reuse immutable rows so that ListBox selection
        // and the detail panel survive refresh without selecting the source again.
        var newest = history.Count > 0 ? history[^1].Sequence : long.MaxValue;
        var desired = history.Concat(ActivityEntries.Where(entry => entry.Sequence > newest))
            .DistinctBy(entry => entry.Sequence).OrderBy(entry => entry.Sequence).TakeLast(500).ToArray();
        var ids = desired.Select(entry => entry.Sequence).ToHashSet();
        for (var index = ActivityEntries.Count - 1; index >= 0; index--)
        {
            if (ids.Contains(ActivityEntries[index].Sequence)) continue;
            _activitySequences.Remove(ActivityEntries[index].Sequence);
            ActivityEntries.RemoveAt(index);
        }
        for (var index = 0; index < desired.Length; index++)
        {
            var existing = -1;
            for (var current = index; current < ActivityEntries.Count; current++)
                if (ActivityEntries[current].Sequence == desired[index].Sequence) { existing = current; break; }
            if (existing == index) continue;
            if (existing >= 0) ActivityEntries.Move(existing, index);
            else
            {
                ActivityEntries.Insert(index, desired[index]);
                _activitySequences.Add(desired[index].Sequence);
            }
        }
        if (SelectedActivityEntry is { } selected && !ids.Contains(selected.Sequence)) SelectedActivityEntry = null;
        SetActivityStatus(status);
    }

    private void InitializeLogs() => JumpToLogSourceCommand = new AsyncCommand(async () =>
    {
        if (SelectedActivityEntry?.ObjectId is not { } id || !CanUseNativeTools) return;
        await LocatePickedAsync(id, ++_pickGeneration);
    }, ReportError, () => CanUseNativeTools && SelectedActivityEntry?.ObjectId is not null);

    private void RefreshLogDetails()
    {
        Raise(nameof(SelectedLogTemplate));
        Raise(nameof(SelectedLogMetadata));
        Raise(nameof(SelectedLogParameters));
        Raise(nameof(SelectedLogException));
        Raise(nameof(SelectedLogScopes));
        JumpToLogSourceCommand?.Refresh();
    }

    private static string LogValue(LogParameterInfo value) => value.State switch
    {
        LogValueState.Null => "null", LogValueState.Unavailable => "[不可用；未调用 ToString]", _ => value.Value ?? ""
    };

    private static string FormatLogException(LogExceptionInfo? exception)
    {
        if (exception is null) return "无已捕获的异常";
        var text = new StringBuilder();
        Append(exception, 0);
        return text.ToString();
        void Append(LogExceptionInfo item, int depth)
        {
            text.Append(' ', depth * 2).Append(item.TypeName).Append(": ").AppendLine(item.Message);
            if (item.IsCycle) text.AppendLine("[循环引用]");
            if (item.IsTruncated) text.AppendLine("[异常链已截断]");
            if (!string.IsNullOrEmpty(item.StackTrace)) text.AppendLine(item.StackTrace);
            foreach (var child in item.InnerExceptions) Append(child, depth + 1);
        }
    }
}
