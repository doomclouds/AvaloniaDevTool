using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed class RuntimeRowViewModel
{
    public RuntimeRowViewModel(RuntimeMemberInfo member, IReadOnlySet<long> ancestors,
        Func<long, string, Task> open, Func<bool> canOpen)
        : this(member.Name, $"{member.Kind} · {member.DeclaringType} · 声明 {member.DeclaredType}",
            member.Value, null, member.Error, member.ErrorTruncated, ancestors, open, canOpen) { }

    public RuntimeRowViewModel(RuntimeItemInfo item, IReadOnlySet<long> ancestors,
        Func<long, string, Task> open, Func<bool> canOpen)
        : this($"[{item.Index}]", item.Key is null ? "IList 元素" : "IDictionary 条目", item.Value,
            item.Key, item.Error, item.ErrorTruncated, ancestors, open, canOpen) { }

    private RuntimeRowViewModel(string name, string description, RuntimeValueInfo value, RuntimeValueInfo? key,
        string? error, bool errorTruncated, IReadOnlySet<long> ancestors,
        Func<long, string, Task> open, Func<bool> canOpen)
    {
        Name = name;
        Description = description;
        Value = value;
        Key = key;
        Error = error ?? "";
        ErrorTruncated = errorTruncated;
        IsCycleReference = value.ValueObjectId is { } id && ancestors.Contains(id) && !value.IsSelfReference;
        IsKeyCycleReference = key?.ValueObjectId is { } keyId && ancestors.Contains(keyId) && !key.IsSelfReference;
        OpenCommand = new AsyncCommand(() => value.ValueObjectId is { } id ? open(id, name) : Task.CompletedTask,
            _ => { }, () => canOpen() && !HasError && value.ValueObjectId is not null);
        OpenKeyCommand = new AsyncCommand(() => key?.ValueObjectId is { } id ? open(id, name + " 键") : Task.CompletedTask,
            _ => { }, () => canOpen() && !HasError && key?.ValueObjectId is not null);
    }

    public string Name { get; }
    public string Description { get; }
    public RuntimeValueInfo Value { get; }
    public RuntimeValueInfo? Key { get; }
    public string Preview => Value.Preview;
    public string TypeName => Value.TypeName;
    public long? ValueObjectId => Value.ValueObjectId;
    public bool IsNull => Value.IsNull;
    public bool IsSelfReference => Value.IsSelfReference;
    public bool IsCycleReference { get; }
    public bool IsKeyCycleReference { get; }
    public bool Truncated => Value.Truncated;
    public string Error { get; }
    public bool HasError => Error.Length > 0;
    public bool ErrorTruncated { get; }
    public bool HasKey => Key is not null;
    public string KeyPreview => Key?.Preview ?? "";
    public string KeyTypeName => Key?.TypeName ?? "";
    public string KeyFlags => Key is null ? "" : ValueFlags(Key, IsKeyCycleReference);
    public string Flags => ValueFlags(Value, IsCycleReference) + (ErrorTruncated ? " · 错误信息已截断" : "");
    public string OpenLabel => IsSelfReference ? "查看自身" : IsCycleReference ? "返回祖先" : "打开值";
    public AsyncCommand OpenCommand { get; }
    public AsyncCommand OpenKeyCommand { get; }
    public void RefreshCommands() { OpenCommand.Refresh(); OpenKeyCommand.Refresh(); }

    private static string ValueFlags(RuntimeValueInfo value, bool cycle)
    {
        var flags = new List<string> { "只读" };
        if (value.IsNull) flags.Add("null");
        if (value.ValueObjectId is { } id) flags.Add($"对象 #{id}");
        if (value.IsSelfReference) flags.Add("自身引用");
        else if (cycle) flags.Add("循环引用（祖先）");
        if (value.Truncated) flags.Add("预览已截断");
        return string.Join(" · ", flags);
    }
}
