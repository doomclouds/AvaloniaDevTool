using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed class ResourceEntryViewModel : ObservableObject
{
    private ResourceEntryInfo _entry;
    private string _draft;
    private string? _error;
    private ResourceQueryEntryInfo? _queryEntry;
    public ResourceEntryViewModel(long dictionaryId, ResourceEntryInfo entry,
        Func<ResourceEntryViewModel, Task> apply, Func<ResourceEntryViewModel, Task> reset)
    {
        DictionaryId = dictionaryId;
        _entry = entry;
        _draft = entry.Value ?? "";
        ApplyCommand = new AsyncCommand(() => apply(this), ShowError, () => CanEdit);
        ResetCommand = new AsyncCommand(() => reset(this), ShowError, () => IsOverridden);
    }
    public ResourceEntryViewModel(ResourceQueryEntryInfo entry,
        Func<ResourceEntryViewModel, Task> apply, Func<ResourceEntryViewModel, Task> reset,
        Func<ResourceEntryViewModel, Task>? read = null)
        : this(entry.OwnerDictionaryId, ToLegacy(entry), apply, reset)
    {
        _queryEntry = entry;
        ReadCommand = new AsyncCommand(() => read?.Invoke(this) ?? Task.CompletedTask, ShowError, () => read is not null);
    }
    public long DictionaryId { get; }
    public long KeyObjectId => _queryEntry?.KeyObjectId ?? 0;
    public long? ValueObjectId => _queryEntry?.ValueObjectId;
    public string? StringKey => _queryEntry?.StringKey ?? (_queryEntry is null ? Key : null);
    public string ProviderPath => _queryEntry?.ProviderPath ?? "";
    public string Provenance => _queryEntry is { } entry
        ? $"{entry.ProviderPath} · 字典 #{entry.OwnerDictionaryId} · {entry.Scope}" + (entry.Theme is { } theme ? $" · 主题 {theme}" : "")
        : "";
    public bool IsTruncated => _queryEntry is { } entry && (entry.KeyTruncated || entry.ValueTruncated);
    public string Key => _entry.Key;
    public string KeyType => _entry.KeyType;
    public string TypeName => _entry.TypeName;
    public bool CanEdit => _entry.CanEdit;
    public bool IsReadOnly => !CanEdit;
    public bool IsOverridden => _entry.IsOverridden;
    public string Draft { get => _draft; set => Set(ref _draft, value); }
    public string? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand ResetCommand { get; }
    public AsyncCommand? ReadCommand { get; }
    internal void Refresh(ResourceQueryEntryInfo entry)
    {
        _queryEntry = entry;
        Refresh(ToLegacy(entry));
        Raise(nameof(Provenance));
        Raise(nameof(IsTruncated));
        Raise(nameof(ValueObjectId));
    }
    internal void Refresh(ResourceEntryInfo entry)
    {
        _entry = entry;
        Draft = entry.Value ?? "";
        Error = null;
        Raise(nameof(CanEdit));
        Raise(nameof(IsReadOnly));
        Raise(nameof(IsOverridden));
        ApplyCommand.Refresh();
        ResetCommand.Refresh();
    }
    private void ShowError(Exception error) => Error = error is RpcException rpc ? $"[{rpc.Code}] {rpc.Message}" : error.Message;
    private static ResourceEntryInfo ToLegacy(ResourceQueryEntryInfo entry) =>
        new(entry.Key, entry.KeyType, entry.TypeName, entry.Value, entry.CanEdit, entry.IsOverridden);
}

