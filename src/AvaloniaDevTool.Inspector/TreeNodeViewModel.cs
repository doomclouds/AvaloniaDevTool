using System.Collections.ObjectModel;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed class TreeNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isLoading;
    private bool _hasMoreChildren;
    private int _totalChildrenCount;
    internal bool HasLoadedPage { get; set; }
    internal string? NextChildrenCursor { get; set; }
    internal Task? LoadingTask { get; set; }
    internal bool IsLoaded { get; set; }
    internal int Generation { get; }
    internal TreeNodeViewModel? Parent { get; }
    internal Action<TreeNodeViewModel>? Expanded { get; set; }
    internal Action<TreeNodeViewModel>? Collapsed { get; set; }
    internal CancellationTokenSource? LoadCancellation { get; set; }

    internal TreeNodeViewModel(TreeNodeInfo node, int generation, TreeNodeViewModel? parent = null)
    {
        Node = node;
        Generation = generation;
        Parent = parent;
        if (node.HasChildren)
            Children.Add(new TreeNodeViewModel(new TreeNodeInfo(0, "展开以加载", null, null, false), generation, this));
    }

    public TreeNodeInfo Node { get; }
    public long Id => Node.Id;
    public bool IsSynthetic => Node.Role is TreeNodeRole.TemplateGroup or TreeNodeRole.ContentSlot;
    public string TypeName => Node.Role switch
    {
        TreeNodeRole.TemplateGroup => "模板 · Template",
        TreeNodeRole.ContentSlot => "内容插槽 · Content slot",
        _ => Node.TypeName.Split('.').Last()
    };
    public string Name => IsSynthetic || string.IsNullOrEmpty(Node.Name) ? "" : "#" + Node.Name;
    public string RoleLabel => Node.Role switch
    {
        TreeNodeRole.TemplateGroup => "边界",
        TreeNodeRole.TemplateRoot => "模板根",
        TreeNodeRole.TemplatePart => "模板部件",
        TreeNodeRole.ContentSlot => "内容引用",
        _ => ""
    };
    public string Preview => Node.Preview ?? "";
    public string Description => string.Join(" ", new[] { TypeName, Name, RoleLabel, Preview }.Where(part => part.Length > 0));
    public bool IsPlaceholder => Id == 0;
    public ObservableCollection<TreeNodeViewModel> Children { get; } = [];
    public bool IsLoading { get => _isLoading; internal set => Set(ref _isLoading, value); }
    public bool HasMoreChildren { get => _hasMoreChildren; internal set => Set(ref _hasMoreChildren, value); }
    public int TotalChildrenCount { get => _totalChildrenCount; internal set => Set(ref _totalChildrenCount, value); }
    public string ChildrenPageSummary => HasLoadedPage ? $"已加载 {Children.Count(child => !child.IsPlaceholder)} / {TotalChildrenCount} 个直接子项" : "尚未加载直接子项";

    internal void UpdatePage(bool hasMore, int total, string? cursor)
    {
        HasLoadedPage = true;
        HasMoreChildren = hasMore;
        TotalChildrenCount = total;
        NextChildrenCursor = cursor;
        IsLoaded = !hasMore;
        Raise(nameof(ChildrenPageSummary));
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && !IsPlaceholder)
            {
                if (value) Expanded?.Invoke(this);
                else Collapsed?.Invoke(this);
            }
        }
    }

    internal void CollapseAndClear()
    {
        LoadCancellation?.Cancel();
        _isExpanded = false;
        Raise(nameof(IsExpanded));
        IsLoaded = false;
        HasLoadedPage = false;
        HasMoreChildren = false;
        TotalChildrenCount = 0;
        NextChildrenCursor = null;
        Children.Clear();
        if (Node.HasChildren)
            Children.Add(new TreeNodeViewModel(new TreeNodeInfo(0, "展开以加载", null, null, false), Generation, this));
    }

    internal void SetExpandedWithoutLoading()
    {
        if (!_isExpanded) { _isExpanded = true; Raise(nameof(IsExpanded)); }
    }

    internal IEnumerable<TreeNodeViewModel> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var descendant in child.DescendantsAndSelf())
                yield return descendant;
    }
}

