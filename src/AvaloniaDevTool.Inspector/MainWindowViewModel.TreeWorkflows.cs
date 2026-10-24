using System.Text.Json;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private CancellationTokenSource? _expansionCancellation;
    private bool _isExpandingTree;
    private string _treeExpansionNotice = "递归展开有节点和深度预算，可随时取消。";
    public Func<string, Task>? CopyTreeTextAsync { get; set; }
    public int ExpansionMaxNodes { get; set; } = 1000;
    public int ExpansionMaxDepth { get; set; } = 32;
    public int ExpansionBatchSize { get; set; } = 32;
    public bool ExpansionIncludeTemplates { get; set; } = true;
    public bool IsExpandingTree { get => _isExpandingTree; private set { if (Set(ref _isExpandingTree, value)) RefreshTreeWorkflowCommands(); } }
    public string TreeExpansionNotice { get => _treeExpansionNotice; private set => Set(ref _treeExpansionNotice, value); }
    public AsyncCommand ExpandNodeCommand { get; private set; } = null!;
    public AsyncCommand ExpandSubtreeCommand { get; private set; } = null!;
    public AsyncCommand CollapseSubtreeCommand { get; private set; } = null!;
    public AsyncCommand CancelTreeExpansionCommand { get; private set; } = null!;
    public AsyncCommand CopySelectorCommand { get; private set; } = null!;
    public AsyncCommand CopyElementMetadataCommand { get; private set; } = null!;

    private void InitializeTreeWorkflows()
    {
        InitializeTreePaging();
        ExpandNodeCommand = new AsyncCommand(async () =>
        {
            if (SelectedNode is not { IsPlaceholder: false } node) return;
            node.IsExpanded = true;
            await LoadChildrenAsync(node, _treeCancellation?.Token ?? _lifetime.Token);
        }, ReportError, HasTreeWorkflowSelection);
        ExpandSubtreeCommand = new AsyncCommand(async () => _ = await ExpandSubtreeAsync(SelectedNode,
            new(ExpansionMaxNodes, ExpansionMaxDepth, ExpansionBatchSize, ExpansionIncludeTemplates)), ReportError,
            () => HasTreeWorkflowSelection() && !IsExpandingTree);
        CollapseSubtreeCommand = new AsyncCommand(() => CollapseSubtreeAsync(SelectedNode), ReportError, HasTreeWorkflowSelection);
        CancelTreeExpansionCommand = new AsyncCommand(() => { CancelTreeExpansion(); return Task.CompletedTask; }, ReportError, () => IsExpandingTree);
        CopySelectorCommand = new AsyncCommand(CopySelectedSelectorAsync, ReportError, HasSelectedNativeElement);
        CopyElementMetadataCommand = new AsyncCommand(CopySelectedMetadataAsync, ReportError, HasSelectedNativeElement);
    }

    private bool HasTreeWorkflowSelection() => IsConnected && SelectedNode is { IsPlaceholder: false };
    private void RefreshTreeWorkflowCommands()
    {
        RefreshTreePagingState();
        ExpandNodeCommand?.Refresh(); ExpandSubtreeCommand?.Refresh(); CollapseSubtreeCommand?.Refresh();
        CancelTreeExpansionCommand?.Refresh(); CopySelectorCommand?.Refresh(); CopyElementMetadataCommand?.Refresh();
    }

    public void CancelTreeExpansion() => _expansionCancellation?.Cancel();

    public async Task<TreeExpansionResult> ExpandSubtreeAsync(TreeNodeViewModel? root, TreeExpansionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (options.MaxNodes is < 1 or > 20000 || options.MaxDepth is < 0 or > 512 || options.BatchSize is < 1 or > 256)
            throw new RpcException("invalid_request", "MaxNodes must be 1..20000, MaxDepth 0..512 and BatchSize 1..256.");
        if (!IsConnected || root is null || root.IsPlaceholder || root.Generation != _treeGeneration) return new(0, 0, false, false);
        CancelTreeExpansion();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _treeCancellation?.Token ?? _lifetime.Token);
        _expansionCancellation = operation;
        IsExpandingTree = true;
        var generation = _treeGeneration;
        var queue = new Queue<(TreeNodeViewModel Node, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        var scheduled = 1;
        var expanded = 0;
        var truncated = false;
        var cancelled = false;
        try
        {
            while (queue.TryDequeue(out var entry))
            {
                operation.Token.ThrowIfCancellationRequested();
                if (generation != _treeGeneration) { cancelled = true; break; }
                if (entry.Node.IsPlaceholder || !options.IncludeTemplates && entry.Node.Node.Role == TreeNodeRole.TemplateGroup) continue;
                if (visited == options.MaxNodes) { truncated = true; break; }
                visited++;
                if (!entry.Node.Node.HasChildren) continue;
                if (entry.Depth >= options.MaxDepth) { truncated = true; continue; }
                entry.Node.SetExpandedWithoutLoading();
                await LoadChildrenAsync(entry.Node, operation.Token, Math.Max(0, options.MaxNodes - scheduled));
                truncated |= !entry.Node.IsLoaded;
                expanded++;
                foreach (var child in entry.Node.Children.Where(child => !child.IsPlaceholder))
                {
                    if (scheduled == options.MaxNodes) { truncated = true; break; }
                    scheduled++;
                    if (options.IncludeTemplates || child.Node.Role != TreeNodeRole.TemplateGroup)
                        queue.Enqueue((child, entry.Depth + 1));
                }
                if (visited % options.BatchSize == 0) await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            if (ReferenceEquals(_expansionCancellation, operation))
            {
                _expansionCancellation = null;
                IsExpandingTree = false;
                TreeExpansionNotice = $"已遍历 {visited} 项，展开 {expanded} 个分支" +
                    (cancelled ? "；已取消，保留已加载部分。" : truncated ? "；达到节点或深度预算，结果已截断。" : "；已完成。");
            }
        }
        return new(visited, expanded, truncated, cancelled);
    }

    public async Task CollapseSubtreeAsync(TreeNodeViewModel? root)
    {
        if (root is null || root.IsPlaceholder) return;
        CancelTreeExpansion();
        var nodes = root.DescendantsAndSelf().ToArray();
        foreach (var node in nodes) node.LoadCancellation?.Cancel();
        // Keep the real selected object as the desired selection even when its row is unloaded.
        if (SelectedNode is { IsPlaceholder: false } selected && nodes.Contains(selected)) _desiredSelectionId = selected.Id;
        var restoring = _restoringSelection;
        _restoringSelection = true;
        try { foreach (var node in nodes.Reverse()) node.CollapseAndClear(); }
        finally { _restoringSelection = restoring; }
        if (IsNativeBackend && IsConnected)
            await ReleaseTreeBranchesAsync(nodes.Where(node => !node.IsPlaceholder).Select(node => node.Id).ToArray(), _loadedTreeKind, _lifetime.Token);
        Raise(nameof(TreeSummary));
    }

    private async Task ReleaseLoadedTreeAsync(TreeKind kind, CancellationToken token)
    {
        var nodes = Roots.SelectMany(node => node.DescendantsAndSelf()).Where(node => !node.IsPlaceholder).ToArray();
        foreach (var node in nodes) node.LoadCancellation?.Cancel();
        if (IsNativeBackend && IsConnected && nodes.Length > 0)
            await ReleaseTreeBranchesAsync(nodes.Select(node => node.Id).ToArray(), kind, token);
    }

    private async Task ReleaseTreeBranchesAsync(long[] ids, TreeKind kind, CancellationToken token)
    {
        var sessionId = _client.Session?.SessionId;
        try
        {
            foreach (var batch in ids.Chunk(20000))
            {
                token.ThrowIfCancellationRequested();
                // IDs from an old tree must never release branches in a replacement session.
                if (!IsConnected || _client.Session?.SessionId != sessionId) return;
                await _client.ReleaseTreeAsync(batch, kind, token);
            }
        }
        catch (RpcException exception) when (exception.Code is "method_not_found" or "unknown_method" or "unsupported")
        { TreeExpansionNotice = "目标 Agent 不支持分支观察释放；请升级目标 Agent 后使用完整树生命周期能力。"; }
    }

    public async Task<string> GetSelectedSelectorTextAsync(CancellationToken token = default)
    {
        if (!HasSelectedNativeElement()) throw new RpcException("unsupported_backend", "Selectors require a real element in the native backend.");
        var result = await _client.GetElementSelectorAsync(SelectedNode!.Id, token);
        if (!result.Supported || result.Selector is null) throw new RpcException("unsupported_selector", result.Detail ?? "The selector is unsupported.");
        return result.Selector;
    }

    private async Task CopySelectedSelectorAsync()
    {
        var nodeId = SelectedNode?.Id;
        var result = await _client.GetElementSelectorAsync(nodeId!.Value, _lifetime.Token);
        if (!result.Supported || result.Selector is null) throw new RpcException("unsupported_selector", result.Detail ?? "The selector is unsupported.");
        if (SelectedNode?.Id != nodeId) return;
        await WriteTreeClipboardAsync(result.Selector);
        Status = result.IsUnique ? "已复制 Avalonia Selector；当前会话内匹配唯一。" : $"已复制 Selector；当前匹配 {result.MatchCount} 项，唯一性未确认。";
    }

    private async Task CopySelectedMetadataAsync()
    {
        var nodeId = SelectedNode?.Id;
        var metadata = await _client.GetElementMetadataAsync(nodeId!.Value, _lifetime.Token);
        if (SelectedNode?.Id != nodeId) return;
        await WriteTreeClipboardAsync(JsonSerializer.Serialize(metadata, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }));
        Status = "已复制元素元数据 JSON；ObjectId 仅在 SessionId 对应的原生会话内有效。";
    }

    private Task WriteTreeClipboardAsync(string text) => CopyTreeTextAsync?.Invoke(text) ??
        throw new RpcException("unsupported_clipboard", "The current window has no clipboard provider.");
}

