using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

internal sealed partial class AgentSession
{
    private TreeChildrenPage GetChildrenPage(TreeChildrenPageRequest request)
    {
        ValidateTreeKind(request.Kind);
        if (request.Offset is < 0 or > 20000 || request.Count is < 1 or > 2000 || request.Cursor?.Length > 128)
            throw new RpcException("invalid_request",
                "Offset must be 0..20000, Count 1..2000 and Cursor at most 128 characters.");
        var parent = _objects.Get(request.ObjectId);
        // Validate attachment in every tree kind, including retained but detached real objects.
        GetAncestors(new TreeAncestorsRequest(request.ObjectId, request.Kind));
        var children = ChildObjects(parent, request.Kind).Take(20001).ToArray();
        if (children.Length > 20000)
            throw new RpcException("tree_limit",
                "A tree branch exceeds the 20000-child safety limit; no partial result was returned.");
        if (request.Cursor is not null && !_treeObservations.ContainsKey((request.ObjectId, request.Kind)))
            throw new RpcException("cursor_invalidated",
                "The branch observation was released or this cursor belongs to another tree. Restart at offset zero.");
        ObserveTree(new TreeObserveRequest(request.ObjectId, request.Kind));
        var observation = _treeObservations[(request.ObjectId, request.Kind)];
        var offset = request.Offset;
        var fingerprint = Fingerprint(children);

        string Cursor(int next) => next.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"{_info.SessionId}:{request.ObjectId}:{request.Kind}:{observation.CursorIdentity}:{observation.Revision}:{next}:{fingerprint}")));

        if (request.Cursor is { } cursor)
        {
            var separator = cursor.IndexOf(':');
            if (separator <= 0 ||
                !int.TryParse(cursor.AsSpan(0, separator),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out offset) || offset is < 0 or > 20000)
                throw new RpcException("invalid_cursor", "The tree continuation cursor is malformed.");
            if (request.Offset != 0 && request.Offset != offset)
                throw new RpcException("invalid_request", "Offset disagrees with the continuation cursor.");
            if (!string.Equals(cursor, Cursor(offset), StringComparison.Ordinal))
                throw new RpcException("cursor_invalidated",
                    "The tree branch changed or the cursor belongs to another session, parent or tree kind. Restart at offset zero.");
        }

        if (offset > children.Length)
            throw new RpcException("invalid_request", "Offset exceeds the current direct-child count.");
        var page = children.Skip(offset).Take(request.Count).Select(child => Describe(child, request.Kind)).ToArray();
        var next = offset + page.Length;
        var more = next < children.Length;
        return new(page, offset, children.Length, more, more ? Cursor(next) : null);
    }

    private TemplateParentSelectorResult GetTemplateParentSelector(long id)
    {
        var target = _objects.Get(id);
        if (target is MergedTree.SyntheticNode synthetic)
            synthetic.Validate();
        else if (target is Avalonia.Visual)
            GetAncestors(new TreeAncestorsRequest(id, TreeKind.Visual));
        var owner = target is MergedTree.SyntheticNode
            ? _mergedTree.Metadata(target).Owner
            : (target as StyledElement)?.TemplatedParent;
        if (owner is not StyledElement styled)
            return new(id, null, new(false, null, false, 0, false, "This node has no current template parent."));
        var ownerId = Observe(styled);
        return new(id, ownerId, ElementSelectorBuilder.Build(styled, RefreshRoots()));
    }
}
