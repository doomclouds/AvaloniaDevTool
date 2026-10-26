using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>A logical-content tree with explicit template groups and non-owning slot references.</summary>
internal sealed class MergedTree
{
    private const int MaximumChildren = 20000;
    private readonly ConditionalWeakTable<TemplatedControl, TemplateState> _templates = new();
    private readonly ConditionalWeakTable<ContentPresenter, ContentSlot> _slots = new();

    public void Invalidate(TemplatedControl owner)
    {
        if (_templates.TryGetValue(owner, out var state))
            state.Invalidate();
    }

    public IReadOnlyList<object> Children(object parent)
    {
        if (parent is SyntheticNode synthetic)
        {
            synthetic.Validate();
            return parent is TemplateGroup group ? group.Roots.Cast<object>().ToArray() : [];
        }

        var result = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance) { parent };

        void Add(object item)
        {
            if (OverlayService.IsDiagnosticVisual(item) || item is TopLevel ||
                item.GetType().FullName == "Avalonia.Controls.TopLevelHost" || !seen.Add(item))
                return;
            if (item is Visual child && parent is Visual host && TopLevel.GetTopLevel(child) is { } childRoot &&
                TopLevel.GetTopLevel(host) is { } hostRoot && !ReferenceEquals(childRoot, hostRoot))
                return;
            if (result.Count >= MaximumChildren)
                throw new RpcException("tree_limit", "A merged tree branch exceeds the 20000-node safety limit.");
            result.Add(item);
        }

        if (parent is ILogical logical)
            foreach (var child in logical.LogicalChildren)
                Add(child);
        if (parent is TemplatedControl owner)
        {
            var group = Group(owner);
            if (group.Roots.Count > 0)
                Add(group);
            return result;
        }

        if (parent is not Visual visual)
            return result;
        var slotContent = SlotContent(parent);
        foreach (var child in visual.GetVisualChildren())
        {
            if (ReferenceEquals(child, slotContent))
                continue;
            // A logical child owned elsewhere is represented at that logical owner.
            if (child is ILogical { LogicalParent: { } logicalParent } && !ReferenceEquals(logicalParent, parent))
                continue;
            Add(child);
        }

        if (parent is ContentPresenter presenter && slotContent is not null)
        {
            if (!_slots.TryGetValue(presenter, out var slot) || !slot.HasContent(slotContent))
            {
                _slots.Remove(presenter);
                slot = new ContentSlot(presenter, slotContent);
                _slots.Add(presenter, slot);
            }

            Add(slot);
        }

        return result;
    }

    public object? Parent(object value)
    {
        if (value is SyntheticNode synthetic)
        {
            synthetic.Validate();
            return synthetic.Parent;
        }

        if (value is ILogical { LogicalParent: { } logicalParent } &&
            Children(logicalParent).Any(child => ReferenceEquals(child, value)))
            return logicalParent;
        if (value is Visual visual && visual.GetVisualParent() is { } visualParent)
        {
            if (visualParent is TemplatedControl owner)
            {
                var group = Group(owner);
                if (group.Roots.Any(root => ReferenceEquals(root, value)))
                    return group;
            }

            if (Children(visualParent).Any(child => ReferenceEquals(child, value)))
                return visualParent;
        }

        return null;
    }

    public (TreeNodeRole Role, object? Owner, object? Content) Metadata(object value)
    {
        if (value is TemplateGroup group)
            return (TreeNodeRole.TemplateGroup, group.Parent, null);
        if (value is ContentSlot slot)
            return (TreeNodeRole.ContentSlot, TemplateOwner(slot.Parent), slot.Content);
        if (value is StyledElement { TemplatedParent: { } owner })
        {
            var root = value is Visual visual && ReferenceEquals(visual.GetVisualParent(), owner);
            return (root ? TreeNodeRole.TemplateRoot : TreeNodeRole.TemplatePart, owner, null);
        }

        return (TreeNodeRole.Element, null, null);
    }

    private TemplateGroup Group(TemplatedControl owner) =>
        _templates.GetValue(owner, static target => new TemplateState(target)).Current;

    private static object? TemplateOwner(object? element) => (element as StyledElement)?.TemplatedParent;

    private static Control? SlotContent(object parent)
    {
        if (parent is not ContentPresenter { Child: { } content } presenter ||
            presenter.TemplatedParent is not ILogical owner)
            return null;
        return owner.LogicalChildren.Any(child => ReferenceEquals(child, content)) ? content : null;
    }

    internal abstract class SyntheticNode
    {
        public abstract object? Parent { get; }
        public abstract string Label { get; }
        public abstract void Validate();

        protected static RpcException Expired() => new("object_expired",
            "The template node is no longer current. Refresh the merged tree.");
    }

    internal sealed class TemplateGroup : SyntheticNode
    {
        private readonly TemplateState _state;
        private readonly WeakReference<TemplatedControl> _owner;
        private readonly WeakReference<Visual>[] _roots;

        internal TemplateGroup(TemplateState state, TemplatedControl owner, Visual[] roots)
        {
            _state = state;
            _owner = new(owner);
            _roots = roots.Select(root => new WeakReference<Visual>(root)).ToArray();
        }

        public override object? Parent => _owner.TryGetTarget(out var owner) ? owner : null;
        public override string Label => "模板 · Template";

        public IReadOnlyList<Visual> Roots => _roots
            .Select(reference => reference.TryGetTarget(out var root) ? root : null)
            .OfType<Visual>().ToArray();

        public override void Validate()
        {
            if (Parent is null || !ReferenceEquals(_state.Current, this))
                throw Expired();
        }
    }

    internal sealed class ContentSlot(ContentPresenter presenter, Control content) : SyntheticNode
    {
        private readonly WeakReference<ContentPresenter> _presenter = new(presenter);
        private readonly WeakReference<Control> _content = new(content);
        public override object? Parent => _presenter.TryGetTarget(out var target) ? target : null;
        public object? Content => _content.TryGetTarget(out var target) ? target : null;
        public override string Label => "内容插槽 · Content slot";
        public bool HasContent(object value) => ReferenceEquals(Content, value);

        public override void Validate()
        {
            if (Parent is not { } parent || Content is not { } current ||
                !ReferenceEquals(SlotContent(parent), current))
                throw Expired();
        }
    }

    internal sealed class TemplateState(TemplatedControl owner)
    {
        private readonly WeakReference<TemplatedControl> _owner = new(owner);
        private TemplateGroup? _current;
        public void Invalidate() => _current = null;

        public TemplateGroup Current
        {
            get
            {
                if (!_owner.TryGetTarget(out var target))
                    throw new RpcException("object_expired", "The template owner no longer exists.");
                var logical = ((ILogical)target).LogicalChildren;
                var roots = target.Template is null
                    ? []
                    : target.GetVisualChildren()
                        .Where(child =>
                            !OverlayService.IsDiagnosticVisual(child) &&
                            !logical.Any(item => ReferenceEquals(item, child)))
                        .Take(MaximumChildren + 1).ToArray();
                if (roots.Length > MaximumChildren)
                    throw new RpcException("tree_limit", "The template has too many visual roots.");
                if (_current is null || !_current.Roots.SequenceEqual(roots, ReferenceEqualityComparer.Instance))
                    _current = new TemplateGroup(this, target, roots);
                return _current;
            }
        }
    }
}
