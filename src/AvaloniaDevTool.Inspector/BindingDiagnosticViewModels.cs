using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed record BindingDetailRowViewModel(string Name, string Value, string State, string? TypeName = null,
    string? Reason = null, string? ObjectReference = null, AsyncCommand? NavigateCommand = null, string? NavigateLabel = null)
{
    public bool HasTypeName => !string.IsNullOrEmpty(TypeName);
    public bool HasReason => !string.IsNullOrEmpty(Reason);
    public bool HasObjectReference => !string.IsNullOrEmpty(ObjectReference);
    public bool HasNavigation => NavigateLabel is not null;
    public bool CanNavigate => NavigateCommand is not null;
}

public sealed class BindingDiagnosticCardViewModel
{
    public BindingDiagnosticCardViewModel(BindingDiagnosticInfo binding, Func<string, BindingDetailValue, AsyncCommand?>? navigation = null,
        bool isNavigationTarget = false)
    {
        PropertyName = binding.PropertyName;
        IsNavigationTarget = isNavigationTarget;
        Expression = binding.Expression;
        Diagnostic = binding.Diagnostic;
        Metadata =
        [
            new("表达式类型", binding.ExpressionType, "已知"),
            new("错误类别", binding.ErrorType, binding.ErrorType == "Unknown" ? "未知" : "已知"),
            new("优先级", binding.Priority, binding.Priority == "Unknown" ? "未知" : "已知"),
            new("是否运行", binding.IsRunning is { } running ? running ? "是" : "否" : "未知",
                binding.IsRunning.HasValue ? "已知" : "未知")
        ];
        Details = binding.Details is { } details ? new BindingDetailsNodeViewModel(details, navigation) : null;
    }

    public string PropertyName { get; }
    public bool IsNavigationTarget { get; }
    public string Expression { get; }
    public string? Diagnostic { get; }
    public bool HasDiagnostic => !string.IsNullOrEmpty(Diagnostic);
    public IReadOnlyList<BindingDetailRowViewModel> Metadata { get; }
    public BindingDetailsNodeViewModel? Details { get; }
    public bool HasDetails => Details is not null;
    public bool HasNoDetails => Details is null;
}

public sealed class BindingDetailsNodeViewModel
{
    private const int MaximumDepth = 8;
    private const int MaximumChildren = 64;
    private static readonly BindingDetailValue MissingValue = new(BindingDetailState.Unavailable,
        Reason: "当前后端未提供此字段。");

    public BindingDetailsNodeViewModel(BindingDetails details, Func<string, BindingDetailValue, AsyncCommand?>? navigation = null)
        : this(details, 0, navigation) { }

    private BindingDetailsNodeViewModel(BindingDetails details, int depth, Func<string, BindingDetailValue, AsyncCommand?>? navigation)
    {
        Kind = details.Kind;
        Fields =
        [
            CreateRow("来源类型", details.SourceType),
            CreateNavigationRow("来源对象", details.Source),
            CreateNavigationRow("DataContext", details.DataContext ?? MissingValue),
            CreateRow("DataContext 来源", details.DataContextOrigin ?? MissingValue),
            CreateRow("路径", details.Path),
            CreateRow("当前值", details.CurrentValue ?? MissingValue),
            CreateRow("模式", details.Mode),
            CreateRow("转换器", details.Converter),
            CreateRow("转换器参数", details.ConverterParameter),
            CreateRow("ConverterCulture", details.ConverterCulture ?? MissingValue),
            CreateRow("Culture 来源", details.ConverterCultureOrigin ?? MissingValue),
            CreateRow("FallbackValue", details.FallbackValue),
            CreateRow("TargetNullValue", details.TargetNullValue),
            CreateRow("更新源触发方式", details.UpdateSourceTrigger),
            CreateRow("StringFormat", details.StringFormat),
            CreateRow("ElementName", details.ElementName),
            CreateRow("RelativeSource", details.RelativeSource),
            CreateNavigationRow("ResourceKey", details.ResourceKey ?? MissingValue)
        ];
        Children = depth < MaximumDepth
            ? details.Children.Take(MaximumChildren).Select(child => new BindingDetailsNodeViewModel(child, depth + 1, navigation)).ToArray()
            : [];
        Errors = details.Errors.Take(MaximumChildren).Select(error => new BindingErrorNodeViewModel(error)).ToArray();
        Gaps = details.Gaps.Take(MaximumChildren).Select(gap => new BindingDetailRowViewModel(gap.Field, "不可用", "缺口", Reason: gap.Reason)).ToArray();
        Truncated = details.Truncated || details.Children.Count > MaximumChildren || details.Errors.Count > MaximumChildren ||
                    details.Gaps.Count > MaximumChildren || depth == MaximumDepth && details.Children.Count > 0;

        BindingDetailRowViewModel CreateNavigationRow(string name, BindingDetailValue value) => CreateRow(name, value) with
        {
            NavigateCommand = navigation?.Invoke(name, value),
            NavigateLabel = name == "ResourceKey" && value.TypeName == typeof(string).FullName ? "查询资源作用域" : "打开对象",
            Reason = value.Reason ?? (value.ObjectId is null && !(name == "ResourceKey" && value.State == BindingDetailState.Known && value.TypeName == typeof(string).FullName)
                ? "此字段没有可导航的 Native 弱对象 ID；不能从文本预览猜测对象或资源标识。" : null)
        };
    }

    public string Kind { get; }
    public IReadOnlyList<BindingDetailRowViewModel> Fields { get; }
    public IReadOnlyList<BindingDetailsNodeViewModel> Children { get; }
    public IReadOnlyList<BindingErrorNodeViewModel> Errors { get; }
    public IReadOnlyList<BindingDetailRowViewModel> Gaps { get; }
    public bool Truncated { get; }
    public bool HasChildren => Children.Count > 0;
    public bool HasErrors => Errors.Count > 0;
    public bool HasGaps => Gaps.Count > 0;
    public string ChildrenHeading => $"子表达式（{Children.Count}）";
    public string GapsHeading => $"诊断缺口（{Gaps.Count}）";

    public static BindingDetailRowViewModel CreateRow(string name, BindingDetailValue value)
    {
        var state = value.State;
        var status = state switch
        {
            BindingDetailState.Known => "已知",
            BindingDetailState.Null => "空值",
            BindingDetailState.Unknown => "未知",
            BindingDetailState.Unavailable => "不可用",
            BindingDetailState.NotApplicable => "不适用",
            _ => "未知"
        };
        var text = state switch
        {
            BindingDetailState.Null => "null",
            BindingDetailState.Known => value.Value ?? string.Empty,
            BindingDetailState.Unknown => "未知",
            BindingDetailState.Unavailable => "不可用",
            BindingDetailState.NotApplicable => "不适用",
            _ => value.Value ?? "未知"
        };
        return new BindingDetailRowViewModel(name, text, status, value.TypeName, value.Reason,
            value.ObjectId is { } id ? $"对象 #{id}（只读引用）" : null);
    }
}

public sealed class BindingErrorNodeViewModel
{
    public BindingErrorNodeViewModel(BindingErrorInfo error) : this(error, 0) { }

    private BindingErrorNodeViewModel(BindingErrorInfo error, int depth)
    {
        Category = error.Category;
        Message = error.Message;
        ExceptionType = error.ExceptionType;
        var innerErrors = error.InnerErrors ?? [];
        InnerErrors = depth < 8
            ? innerErrors.Take(64).Select(inner => new BindingErrorNodeViewModel(inner, depth + 1)).ToArray()
            : [];
        Truncated = error.Truncated || innerErrors.Count > 64 || depth == 8 && innerErrors.Count > 0;
    }

    public string Category { get; }
    public string Message { get; }
    public string? ExceptionType { get; }
    public IReadOnlyList<BindingErrorNodeViewModel> InnerErrors { get; }
    public bool Truncated { get; }
    public bool HasExceptionType => !string.IsNullOrEmpty(ExceptionType);
}

