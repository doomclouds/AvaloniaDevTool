using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed class PropertyRowViewModel : ObservableObject, IDisposable
{
    private PropertyInfoDto _property;
    private string _draft;
    private string? _localError;
    private bool _dirty;
    private bool _updatingDraft;
    private Bitmap? _previewImage;
    private string _previewMessage = "";
    private bool _grouped;
    private bool _showCategoryHeader;
    private PropertyCategoryGroupViewModel? _group;

    internal PropertyRowViewModel(PropertyInfoDto property, Func<PropertyRowViewModel, Task> apply,
        Func<PropertyRowViewModel, Task> clear, Action<Exception> onError,
        Func<PropertyRowViewModel, Task>? preview = null)
    {
        _property = property;
        _draft = property.Value ?? "";
        if (property.Presentation?.Brush is { } brush) BrushEditor = new BrushEditorViewModel(brush, BrushChanged);
        ApplyCommand = new AsyncCommand(() => apply(this), HandleError, () => CanEdit);
        ClearCommand = new AsyncCommand(() => clear(this), HandleError, () => CanClear);
        PreviewCommand = new AsyncCommand(() => preview?.Invoke(this) ?? Task.CompletedTask, HandleError,
            () => HasPreview && PreviewValueId is not null && preview is not null);
        FlagChoices = (_property.Presentation?.Choices ?? []).Select(choice => new PropertyFlagChoiceViewModel(this, choice)).ToArray();

        void HandleError(Exception exception)
        {
            _localError = exception is RpcException rpc ? $"{rpc.Code}: {rpc.Message}" : exception.Message;
            Raise(nameof(Error));
            Raise(nameof(HasError));
            onError(exception);
        }
    }

    public string Id => _property.Id;
    public string Name => _property.Name;
    public string TypeName => _property.TypeName;
    public string DeclaringType => _property.DeclaringType;
    public string Source => _property.ValueSource;
    public string? CurrentValue => _property.Value;
    public string? Error => _localError ?? _property.Error;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool CanEdit => _property.CanEdit && !_property.IsReadOnly;
    public bool IsReadOnly => !CanEdit;
    public bool CanClear => _property.CanClear;
    public bool IsDirty => _dirty;
    public string Category => _property.Presentation?.Category ?? DeclaringType;
    public string MemberKind => _property.Presentation?.MemberKind ?? "Avalonia";
    public string ContextNotice => _property.Presentation?.Context is { IsApplicable: false } context
        ? $"当前布局不使用此属性：{context.Rule}；实际父节点 {context.VisualParentType ?? "无"}。" : "";
    public bool HasContextNotice => ContextNotice.Length > 0;
    public bool IsContextRelevant => _property.Presentation?.Context?.IsApplicable != false;
    public PropertyCategoryGroupViewModel? Group => _group;
    public bool ShowCategoryHeader => _showCategoryHeader;
    public bool IsGroupExpanded => !_grouped || _group?.IsExpanded != false;
    public bool IsVisibleInGroup => IsGroupExpanded || ShowCategoryHeader;
    public double RowMinimumHeight => IsGroupExpanded ? 43 : 0;
    public PropertyEditorKind EditorKind => !CanEdit ? PropertyEditorKind.ReadOnly : _property.Presentation?.Editor ?? PropertyEditorKind.Text;
    public bool IsTextEditor => EditorKind is PropertyEditorKind.Text or PropertyEditorKind.Number or PropertyEditorKind.Color ||
        EditorKind == PropertyEditorKind.SolidColorBrush && BrushEditor?.Kind is null or BrushKind.Solid;
    public bool IsBooleanEditor => EditorKind == PropertyEditorKind.Boolean;
    public bool IsNumberEditor => EditorKind == PropertyEditorKind.Number;
    public bool IsEnumEditor => EditorKind == PropertyEditorKind.Enum;
    public bool IsFlagsEditor => EditorKind == PropertyEditorKind.Flags;
    public bool IsColorEditor => EditorKind == PropertyEditorKind.Color ||
        EditorKind == PropertyEditorKind.SolidColorBrush && BrushEditor?.Kind is null or BrushKind.Solid;
    public bool IsBrushEditor => CanEdit && BrushEditor is not null;
    public BrushEditorViewModel? BrushEditor { get; private set; }
    public BrushDto? BrushDraft => IsBrushEditor ? BrushEditor!.ToDto() : null;
    public IReadOnlyList<string> EnumChoices => _property.Presentation?.Choices?.Select(choice => choice.Name).ToArray() ?? [];
    public string? EnumDraft { get => Draft; set { if (value is not null && !_updatingDraft) Draft = value; } }
    public IReadOnlyList<PropertyFlagChoiceViewModel> FlagChoices { get; }
    public bool? BooleanDraft { get => bool.TryParse(Draft, out var value) ? value : null; set { if (!_updatingDraft && value is { } boolean) Draft = boolean ? "True" : "False"; } }
    public decimal? NumberDraft { get => decimal.TryParse(Draft, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null; set { if (!_updatingDraft && value is { } number) Draft = number.ToString(CultureInfo.InvariantCulture); } }
    public Color DraftColor
    {
        get => Color.TryParse(Draft, out var color) ? color : Colors.Transparent;
        set { if (!_updatingDraft && (!Color.TryParse(Draft, out var color) || color != value)) Draft = value.ToString(); }
    }
    public IBrush CurrentColor => Color.TryParse(CurrentValue, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;
    public string EditorNotice => _property.Presentation?.Notice ?? "";
    public string DraftValidation => IsColorEditor && !Color.TryParse(Draft, out _) ? "无效的颜色值；请使用颜色名称或 #AARRGGBB。" : "";
    public bool HasDraftValidation => DraftValidation.Length > 0;
    public bool HasPreview => _property.Presentation?.Preview is PropertyPreviewKind.Image or PropertyPreviewKind.Geometry;
    public long? PreviewValueId => _property.Presentation?.ValueId;
    public long PreviewVersion => _property.Presentation?.Version ?? 0;
    public Bitmap? PreviewImage => _previewImage;
    public string PreviewMessage => _previewMessage;
    public bool HasPreviewImage => _previewImage is not null;
    public AsyncCommand ApplyCommand { get; }
    public AsyncCommand ClearCommand { get; }
    public AsyncCommand PreviewCommand { get; }

    public string Draft
    {
        get => _draft;
        set
        {
            if (Set(ref _draft, value ?? ""))
            {
                if (EditorKind == PropertyEditorKind.SolidColorBrush) BrushEditor?.SetColor(_draft);
                _dirty = _draft != (_property.Value ?? "") || BrushEditor?.IsDirty == true;
                _localError = null;
                Raise(nameof(IsDirty));
                Raise(nameof(Error));
                Raise(nameof(HasError));
                RefreshDraftEditors();
            }
        }
    }

    internal void Refresh(PropertyInfoDto property, bool preserveDraft)
    {
        var keepBrushDraft = preserveDraft && _dirty;
        _property = property;
        if (!keepBrushDraft)
        {
            if (property.Presentation?.Brush is { } brush)
            {
                if (BrushEditor is null) BrushEditor = new BrushEditorViewModel(brush, BrushChanged);
                else BrushEditor.Reload(brush);
            }
            else BrushEditor = null;
        }
        if (!preserveDraft || !_dirty)
        {
            _draft = property.Value ?? "";
            _dirty = false;
        }
        else _dirty = _draft != (property.Value ?? "") || BrushEditor?.IsDirty == true;
        if (!preserveDraft)
            _localError = null;
        Raise(nameof(Draft));
        Raise(nameof(IsDirty));
        Raise(nameof(Source));
        Raise(nameof(CurrentValue));
        Raise(nameof(CanEdit));
        Raise(nameof(IsReadOnly));
        Raise(nameof(CanClear));
        Raise(nameof(Error));
        Raise(nameof(HasError));
        Raise(nameof(Category));
        Raise(nameof(MemberKind));
        Raise(nameof(EditorKind));
        Raise(nameof(IsTextEditor));
        Raise(nameof(IsBooleanEditor));
        Raise(nameof(IsNumberEditor));
        Raise(nameof(IsEnumEditor));
        Raise(nameof(IsFlagsEditor));
        Raise(nameof(IsColorEditor));
        Raise(nameof(IsBrushEditor));
        Raise(nameof(BrushEditor));
        Raise(nameof(CurrentColor));
        Raise(nameof(EditorNotice));
        Raise(nameof(ContextNotice)); Raise(nameof(HasContextNotice)); Raise(nameof(IsContextRelevant));
        Raise(nameof(HasPreview));
        RefreshDraftEditors();
        if (_previewImage is not null) SetPreview(new PropertyPreviewResult(PropertyPreviewStatus.Stale, Message: "属性已刷新，点击预览读取当前值。"));
        ApplyCommand.Refresh();
        ClearCommand.Refresh();
        PreviewCommand.Refresh();
    }

    private void BrushChanged()
    {
        if (BrushEditor is not { } brush) return;
        if (brush.Kind == BrushKind.Solid && EditorKind == PropertyEditorKind.SolidColorBrush)
        {
            _draft = brush.Color;
            Raise(nameof(Draft));
            RefreshDraftEditors();
        }
        _dirty = brush.IsDirty || _draft != (_property.Value ?? "");
        _localError = null;
        Raise(nameof(IsColorEditor)); Raise(nameof(IsTextEditor));
        Raise(nameof(IsDirty)); Raise(nameof(Error)); Raise(nameof(HasError));
    }

    private void RefreshDraftEditors()
    {
        _updatingDraft = true;
        try
        {
            Raise(nameof(BooleanDraft)); Raise(nameof(NumberDraft)); Raise(nameof(DraftColor));
            Raise(nameof(EnumDraft));
            Raise(nameof(DraftValidation)); Raise(nameof(HasDraftValidation));
            foreach (var choice in FlagChoices) choice.Refresh();
        }
        finally { _updatingDraft = false; }
    }

    internal void SetGroup(PropertyCategoryGroupViewModel? group, bool grouped, bool header)
    {
        _group = group; _grouped = grouped; _showCategoryHeader = header;
        Raise(nameof(Group)); Raise(nameof(ShowCategoryHeader)); Raise(nameof(IsGroupExpanded));
        Raise(nameof(IsVisibleInGroup)); Raise(nameof(RowMinimumHeight));
    }

    internal void SetPreview(PropertyPreviewResult result)
    {
        _previewImage?.Dispose();
        _previewImage = null;
        _previewMessage = result.Message ?? (result.Status == PropertyPreviewStatus.Available ? $"{result.Width} × {result.Height} · 当前属性值" : "无法预览此属性值。");
        if (result is { Status: PropertyPreviewStatus.Available, Base64: { } base64 })
        {
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            _previewImage = new Bitmap(stream);
        }
        Raise(nameof(PreviewImage)); Raise(nameof(PreviewMessage)); Raise(nameof(HasPreviewImage));
    }

    internal ulong FlagBits()
    {
        ulong bits = 0;
        foreach (var name in Draft.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var raw = _property.Presentation?.Choices?.FirstOrDefault(choice => choice.Name == name)?.Value ?? name;
            if (ulong.TryParse(raw, out var unsigned)) bits |= unsigned;
            else if (long.TryParse(raw, out var signed)) bits |= unchecked((ulong)signed);
        }
        return bits;
    }

    internal void ToggleFlag(ulong flag, bool selected)
    {
        if (_updatingDraft) return;
        var bits = selected ? (flag == 0 ? 0 : FlagBits() | flag) : FlagBits() & ~flag;
        // Numeric text preserves unknown bits and aliases; Enum.Parse validates the target type on Apply.
        Draft = _property.Presentation?.Choices?.Any(choice => choice.Value.StartsWith('-')) == true
            ? unchecked((long)bits).ToString(CultureInfo.InvariantCulture) : bits.ToString(CultureInfo.InvariantCulture);
    }

    public void Dispose() => _previewImage?.Dispose();
}

public sealed class PropertyFlagChoiceViewModel : ObservableObject
{
    private readonly PropertyRowViewModel _owner;
    private readonly ulong _bits;
    internal PropertyFlagChoiceViewModel(PropertyRowViewModel owner, PropertyEnumChoice choice)
    {
        _owner = owner; Name = choice.Name;
        _bits = ulong.TryParse(choice.Value, out var unsigned) ? unsigned : unchecked((ulong)long.Parse(choice.Value, CultureInfo.InvariantCulture));
    }
    public string Name { get; }
    public bool IsSelected { get => _bits == 0 ? _owner.FlagBits() == 0 : (_owner.FlagBits() & _bits) == _bits; set => _owner.ToggleFlag(_bits, value); }
    internal void Refresh() => Raise(nameof(IsSelected));
}

