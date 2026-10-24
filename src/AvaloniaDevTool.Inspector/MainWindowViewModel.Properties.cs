using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private int _propertySortIndex;
    private string _propertyCategory = "全部分类";
    private PropertyRowViewModel? _selectedProperty;
    private bool _includeClrProperties;
    private bool _contextualProperties = true;
    public bool ContextualProperties
    {
        get => _contextualProperties;
        set
        {
            if (!Set(ref _contextualProperties, value)) return;
            FilterProperties();
        }
    }
    private long _propertyLoadSequence;
    private long _appliedPropertyLoadSequence;
    private long? _propertyRowsObjectId;
    private readonly Dictionary<string, PropertyCategoryGroupViewModel> _propertyGroups = [];
    public bool IncludeClrProperties
    {
        get => _includeClrProperties;
        set
        {
            if (!Set(ref _includeClrProperties, value)) return;
            if (CanUseNativeTools && SelectedNode is { IsPlaceholder: false, IsSynthetic: false } node)
                _ = RunSafelyAsync(() => LoadPropertiesAsync(node, _selectionCancellation?.Token ?? _lifetime.Token, true));
        }
    }
    public IReadOnlyList<string> PropertySortOptions { get; } = ["名称 ↑", "名称 ↓", "值 ↑", "值 ↓", "分类 / 名称"];
    public System.Collections.ObjectModel.ObservableCollection<string> PropertyCategories { get; } = ["全部分类"];
    public int PropertySortIndex { get => _propertySortIndex; set { if (Set(ref _propertySortIndex, value)) FilterProperties(); } }
    public string PropertyCategory { get => _propertyCategory; set { if (Set(ref _propertyCategory, value ?? "全部分类")) FilterProperties(); } }
    public PropertyRowViewModel? SelectedProperty { get => _selectedProperty; set => Set(ref _selectedProperty, value); }

    private void UpdatePropertyCategories()
    {
        var categories = new[] { "全部分类" }.Concat(_properties.Select(property => property.Category).Distinct().Order(StringComparer.OrdinalIgnoreCase)).ToArray();
        if (!PropertyCategories.SequenceEqual(categories))
        {
            PropertyCategories.Clear();
            foreach (var category in categories) PropertyCategories.Add(category);
        }
        if (!categories.Contains(PropertyCategory)) PropertyCategory = "全部分类";
        foreach (var category in categories.Skip(1))
            if (!_propertyGroups.ContainsKey(category))
                _propertyGroups.Add(category, new PropertyCategoryGroupViewModel(category, FilterProperties));
    }

    private async Task PreviewPropertyAsync(PropertyRowViewModel row)
    {
        if (!CanUseNativeTools || SelectedNode is not { } node || row.PreviewValueId is not { } valueId) return;
        var version = row.PreviewVersion;
        var token = _selectionCancellation?.Token ?? _lifetime.Token;
        var result = await _client.GetPropertyPreviewAsync(new PropertyPreviewRequest(node.Id, row.Id, valueId, version), token);
        if (!token.IsCancellationRequested && SelectedNode?.Id == node.Id && row.PreviewVersion == version && _properties.Contains(row))
            row.SetPreview(result);
    }

    private void ClearPropertyRows()
    {
        foreach (var row in _properties) row.Dispose();
        _properties.Clear();
        _propertyRowsObjectId = null;
        _propertyGroups.Clear();
        SelectedProperty = null;
        UpdatePropertyCategories();
    }
}

public sealed class PropertyCategoryGroupViewModel(string name, Action changed) : ObservableObject
{
    private bool _expanded = true;
    public string Name => name;
    public string Title => (IsExpanded ? "▼ " : "▶ ") + Name;
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (!Set(ref _expanded, value)) return;
            Raise(nameof(Title));
            changed();
        }
    }
}
