using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Inspector;

public sealed partial class MainWindowViewModel
{
    public bool IsSyntheticSelection => SelectedNode?.IsSynthetic == true;
    public string SyntheticSelectionNotice => SelectedNode is { IsSynthetic: true } node
        ? node.Node.Role == TreeNodeRole.ContentSlot
            ? "内容插槽是指向逻辑内容的合成引用，不是实际控件。请跳转实际内容或模板所属控件后检查属性、对象和元素动作。"
            : "模板组是区分用户内容与模板视觉结构的合成边界，不是实际控件。可展开查看模板，或跳转模板所属控件。"
        : "";
    public AsyncCommand NavigateTemplateOwnerCommand { get; private set; } = null!;
    public AsyncCommand NavigateSlotContentCommand { get; private set; } = null!;

    private void InitializeMergedTree()
    {
        NavigateTemplateOwnerCommand = new AsyncCommand(() => NavigateRelatedTreeObjectAsync(SelectedNode?.Node.TemplateOwnerId),
            ReportError, () => CanUseNativeTools && SelectedNode?.Node.TemplateOwnerId is not null);
        NavigateSlotContentCommand = new AsyncCommand(() => NavigateRelatedTreeObjectAsync(SelectedNode?.Node.SlotContentId),
            ReportError, () => CanUseNativeTools && SelectedNode?.Node.SlotContentId is not null);
    }

    private void RefreshMergedCommands()
    {
        NavigateTemplateOwnerCommand?.Refresh();
        NavigateSlotContentCommand?.Refresh();
        Raise(nameof(IsSyntheticSelection));
        Raise(nameof(SyntheticSelectionNotice));
    }

    private Task NavigateRelatedTreeObjectAsync(long? objectId)
    {
        if (!CanUseNativeTools || objectId is not { } id) return Task.CompletedTask;
        return LocatePickedAsync(id, ++_pickGeneration);
    }
}
