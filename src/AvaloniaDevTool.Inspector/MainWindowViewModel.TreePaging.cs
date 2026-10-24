using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    private const int TreePageSize = 100;
    private SessionInfo? _treePagingCapabilitySession;
    private bool? _treePagingCapabilitySupported;
    public AsyncCommand LoadMoreChildrenCommand { get; private set; } = null!;
    public AsyncCommand CopyTemplateParentSelectorCommand { get; private set; } = null!;
    public AsyncCommand CopySelectorFromTemplateParentCommand { get; private set; } = null!;
    public AsyncCommand NavigateSelectorTemplateParentCommand { get; private set; } = null!;
    public string SelectedChildrenPageSummary => SelectedNode?.ChildrenPageSummary ?? "选择分支以加载直接子项。";

    private void InitializeTreePaging()
    {
        LoadMoreChildrenCommand = new AsyncCommand(async () =>
        {
            if (SelectedNode is { } node) await LoadMoreChildrenAsync(node, _treeCancellation?.Token ?? _lifetime.Token);
        }, ReportError, () => HasTreeWorkflowSelection() && SelectedNode is { HasMoreChildren: true, IsLoading: false });
        CopyTemplateParentSelectorCommand = new AsyncCommand(CopyTemplateParentSelectorAsync, ReportError,
            () => HasTreeWorkflowSelection() && IsNativeBackend);
        CopySelectorFromTemplateParentCommand = new AsyncCommand(CopySelectorFromTemplateParentAsync, ReportError, HasSelectedNativeElement);
        NavigateSelectorTemplateParentCommand = new AsyncCommand(async () =>
        {
            var selected = SelectedNode?.Id;
            if (selected is not { } id) return;
            var result = await _client.GetTemplateParentSelectorAsync(id, _lifetime.Token);
            if (SelectedNode?.Id != selected) return;
            if (result.TemplateParentId is not { } owner)
                throw new RpcException("unsupported_selector", result.Selector.Detail ?? "The element has no template parent.");
            await NavigateRelatedTreeObjectAsync(owner);
        }, ReportError, () => HasTreeWorkflowSelection() && IsNativeBackend);
    }

    private async Task LoadChildrenAsync(TreeNodeViewModel node, CancellationToken cancellationToken, int? maximumChildren = null)
    {
        if (node.IsPlaceholder || node.Generation != _treeGeneration || node.IsLoaded || maximumChildren == 0) return;
        if (node.LoadingTask is { } loading)
        {
            await loading.WaitAsync(cancellationToken);
            if (maximumChildren is { } remaining && node.Children.Count < remaining && !node.IsLoaded)
            {
                // Let the initiating waiter finish clearing its load ownership before starting another page.
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                await LoadChildrenAsync(node, cancellationToken, maximumChildren);
            }
            return;
        }
        if (node.HasLoadedPage && maximumChildren is null) return;
        node.LoadCancellation?.Dispose();
        node.LoadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        node.LoadingTask = LoadChildrenCoreAsync(node, node.LoadCancellation.Token, maximumChildren);
        try { await node.LoadingTask; }
        finally
        {
            node.LoadingTask = null;
            node.LoadCancellation?.Dispose();
            node.LoadCancellation = null;
        }
    }

    private async Task LoadChildrenCoreAsync(TreeNodeViewModel node, CancellationToken cancellationToken, int? maximumChildren)
    {
        node.IsLoading = true;
        RefreshTreePagingState();
        var kind = _loadedTreeKind;
        var session = _client.Session;
        if (IsNativeBackend && !ReferenceEquals(_treePagingCapabilitySession, session))
        {
            _treePagingCapabilitySession = session;
            _treePagingCapabilitySupported = null;
        }
        try
        {
            long? preservedChildId = null;
            if (maximumChildren is null && SelectedNode is { } selected && _desiredSelectionId == selected.Id)
            {
                for (var ancestor = selected; ancestor.Parent is { } parent; ancestor = parent)
                    if (ReferenceEquals(parent, node)) { preservedChildId = ancestor.Id; break; }
            }
            var desired = maximumChildren ?? (preservedChildId is null ? TreePageSize : 20000);
            do
            {
                var offset = node.HasLoadedPage ? node.Children.Count(child => !child.IsPlaceholder) : 0;
                if (offset >= desired) break;
                var count = Math.Min(TreePageSize, desired - offset);
                TreeChildrenPage? page = null;
                if (IsNativeBackend && _treePagingCapabilitySupported is not false)
                {
                    try
                    {
                        page = await _client.GetChildrenPageAsync(node.Id, kind, offset, count, node.NextChildrenCursor, cancellationToken);
                        if (!ReferenceEquals(_client.Session, session)) throw new OperationCanceledException();
                        _treePagingCapabilitySupported = true;
                    }
                    catch (RpcException exception) when (_treePagingCapabilitySupported is null &&
                        exception.Code is "method_not_found" or "unknown_method" or "unsupported")
                    {
                        if (!ReferenceEquals(_client.Session, session)) throw new OperationCanceledException();
                        _treePagingCapabilitySupported = false;
                        TreeExpansionNotice = "旧版 Agent 不支持原生直接子项分页，已使用兼容读取并在本地分页；不提供原生游标或事件版本保证。";
                    }
                }
                if (page is null)
                {
                    var children = await _backend.GetChildrenAsync(node.Id, kind, cancellationToken);
                    if (node.HasLoadedPage && (children.Count < offset ||
                        node.Children.Where(child => !child.IsPlaceholder).Select(child => child.Id)
                            .Where((id, index) => id != children[index].Id).Any()))
                        throw new RpcException("tree_changed", "The compatibility branch's loaded prefix changed. Collapse and reopen it before continuing.");
                    var items = children.Skip(offset).Take(count).ToArray();
                    page = new(items, offset, children.Count, offset + items.Length < children.Count, null);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (node.Generation != _treeGeneration || kind != _loadedTreeKind || IsNativeBackend && !ReferenceEquals(_client.Session, session)) return;
                if (!node.HasLoadedPage) node.Children.Clear();
                foreach (var child in page.Children) node.Children.Add(CreateNode(child, node.Generation, node));
                node.UpdatePage(page.HasMore, page.TotalCount, page.NextCursor);
                if (_desiredSelectionId is { } desiredId && SelectedNode?.Id == desiredId &&
                    node.Children.FirstOrDefault(child => child.Id == desiredId) is { } currentSelection)
                    CommitSelection(currentSelection);
                Raise(nameof(TreeSummary));
                RefreshTreePagingState();
            } while (!node.IsLoaded && (maximumChildren is not null || preservedChildId is { } preserved && node.Children.All(child => child.Id != preserved)));
        }
        catch (RpcException exception) when (exception.Code is "cursor_invalidated" or "tree_changed")
        {
            TreeExpansionNotice = exception.Code == "tree_changed"
                ? "兼容读取的直接子项已变化，本地分页已停止；请折叠后重新展开。已加载行和当前选择已保留。"
                : "直接子项已变化，分页游标失效；请折叠后重新展开此分支。已加载行和当前选择已保留。";
            throw;
        }
        finally
        {
            node.IsLoading = false;
            RefreshTreePagingState();
        }
    }

    public async Task LoadMoreChildrenAsync(TreeNodeViewModel node, CancellationToken cancellationToken = default)
    {
        if (node.Generation != _treeGeneration || node.IsPlaceholder || !node.HasMoreChildren) return;
        await LoadChildrenAsync(node, cancellationToken, node.Children.Count(child => !child.IsPlaceholder) + TreePageSize);
    }

    private async Task<TreeNodeViewModel?> EnsureChildLoadedAsync(TreeNodeViewModel parent, long childId, CancellationToken token)
    {
        await LoadChildrenAsync(parent, token);
        while (true)
        {
            if (parent.Children.FirstOrDefault(child => child.Id == childId) is { } child) return child;
            if (!parent.HasMoreChildren) return null;
            token.ThrowIfCancellationRequested();
            await LoadMoreChildrenAsync(parent, token);
        }
    }

    private void RefreshTreePagingState()
    {
        LoadMoreChildrenCommand?.Refresh();
        CopyTemplateParentSelectorCommand?.Refresh();
        CopySelectorFromTemplateParentCommand?.Refresh();
        NavigateSelectorTemplateParentCommand?.Refresh();
        Raise(nameof(SelectedChildrenPageSummary));
    }

    public async Task<string> GetSelectedTemplateParentSelectorTextAsync(CancellationToken token = default)
    {
        if (!HasTreeWorkflowSelection() || !IsNativeBackend)
            throw new RpcException("unsupported_backend", "Template-parent selectors require the native backend.");
        var result = await _client.GetTemplateParentSelectorAsync(SelectedNode!.Id, token);
        if (!result.Selector.Supported || result.Selector.Selector is null)
            throw new RpcException("unsupported_selector", result.Selector.Detail ?? "The template parent selector is unsupported.");
        return result.Selector.Selector;
    }

    private async Task CopyTemplateParentSelectorAsync()
    {
        var selected = SelectedNode?.Id;
        if (selected is not { } id) return;
        var result = await _client.GetTemplateParentSelectorAsync(id, _lifetime.Token);
        if (!result.Selector.Supported || result.Selector.Selector is null)
            throw new RpcException("unsupported_selector", result.Selector.Detail ?? "The template parent selector is unsupported.");
        if (SelectedNode?.Id != selected) return;
        await WriteTreeClipboardAsync(result.Selector.Selector);
        Status = result.Selector.IsUnique ? "已复制模板父 Selector；当前会话内匹配唯一。" : $"已复制模板父 Selector；匹配 {result.Selector.MatchCount} 项，唯一性未确认。";
    }

    public async Task<ElementSelectorResult> GetSelectedSelectorFromTemplateParentAsync(CancellationToken token = default)
    {
        if (!HasSelectedNativeElement() || SelectedNode is not { } node)
            throw new RpcException("unsupported_backend", "A selector from the template parent requires a real native element.");
        var generation = _treeGeneration;
        var selectionGeneration = _pickGeneration;
        var sessionId = _client.Session?.SessionId;
        var parent = await _client.GetTemplateParentSelectorAsync(node.Id, token);
        if (!IsCurrentTreeSelectorTarget(node, generation, selectionGeneration, sessionId)) throw new OperationCanceledException();
        if (parent.TemplateParentId is null)
            throw new RpcException("unsupported_selector", parent.Selector.Detail ?? "The selected element has no template parent.");
        var result = await _client.GetElementSelectorAsync(node.Id, token);
        if (!IsCurrentTreeSelectorTarget(node, generation, selectionGeneration, sessionId)) throw new OperationCanceledException();
        if (!result.Supported || result.Selector is null)
            throw new RpcException("unsupported_selector", result.Detail ?? "The template-to-part selector is unsupported.");
        if (!result.Selector.Contains("/template/", StringComparison.Ordinal))
            throw new RpcException("unsupported_selector", "The generated selector has no validated path from the template parent to the selected part.");
        return result;
    }

    private bool IsCurrentTreeSelectorTarget(TreeNodeViewModel node, int generation, int selectionGeneration, string? sessionId) =>
        !_disposed && IsConnected && IsNativeBackend && _treeGeneration == generation && node.Generation == generation &&
        _pickGeneration == selectionGeneration && _client.Session?.SessionId == sessionId && ReferenceEquals(SelectedNode, node);

    private async Task CopySelectorFromTemplateParentAsync()
    {
        if (SelectedNode is not { } node) return;
        var generation = _treeGeneration;
        var selectionGeneration = _pickGeneration;
        var sessionId = _client.Session?.SessionId;
        var result = await GetSelectedSelectorFromTemplateParentAsync(_lifetime.Token);
        if (!IsCurrentTreeSelectorTarget(node, generation, selectionGeneration, sessionId)) return;
        await WriteTreeClipboardAsync(result.Selector!);
        if (!IsCurrentTreeSelectorTarget(node, generation, selectionGeneration, sessionId)) return;
        Status = result.IsUnique ? "已复制从模板父到所选部件的 Selector；当前会话内匹配唯一。" :
            $"已复制从模板父到所选部件的 Selector；匹配 {result.MatchCount} 项，唯一性未确认。";
    }
}
