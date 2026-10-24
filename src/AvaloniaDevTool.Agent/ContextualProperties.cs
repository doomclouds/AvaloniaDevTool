using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Only known layout consumers are filtered; unknown attached properties remain visible.</summary>
internal static class ContextualProperties
{
    private static readonly HashSet<string> GridMembers = ["Row", "Column", "RowSpan", "ColumnSpan"];
    private static readonly HashSet<string> CanvasMembers = ["Left", "Top", "Right", "Bottom"];
    private static readonly HashSet<string> RelativeMembers =
    [
        "Above", "Below", "LeftOf", "RightOf", "AlignLeftWith", "AlignTopWith", "AlignRightWith", "AlignBottomWith",
        "AlignHorizontalCenterWith", "AlignVerticalCenterWith", "AlignLeftWithPanel", "AlignTopWithPanel",
        "AlignRightWithPanel", "AlignBottomWithPanel", "AlignHorizontalCenterWithPanel", "AlignVerticalCenterWithPanel"
    ];
    public static PropertyContextInfo? Describe(AvaloniaObject target, AvaloniaProperty property)
    {
        var owner = property.OwnerType;
        var parent = Parent(target);
        bool? applicable = owner == typeof(Grid) && GridMembers.Contains(property.Name) ? parent is Grid
            : owner == typeof(Canvas) && CanvasMembers.Contains(property.Name) ? parent is Canvas
            : owner == typeof(DockPanel) && property.Name == "Dock" ? parent is DockPanel
            : owner == typeof(RelativePanel) && RelativeMembers.Contains(property.Name) ? parent is RelativePanel : null;
        return applicable is { } value
            ? new(value, $"Direct visual child of {owner.Name}", parent?.GetType().FullName) : null;
    }
    public static Visual? Parent(AvaloniaObject target) => (target as Visual)?.GetVisualParent();
}
