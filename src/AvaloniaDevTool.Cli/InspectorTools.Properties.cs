using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Cli;

public sealed partial class InspectorTools
{
    [McpServerTool(Name = "props", ReadOnly = true), Description("Read properties with stable owner-qualified IDs, effective values, priorities and editability. contextual filters only known direct-parent layout properties; unknown attached members remain. includeClr explicitly opts into read-only public CLR getters. Both options default false for compatibility.")]
    public async Task<IReadOnlyList<PropertyInfoDto>> Props(long nodeId, string[]? propertyNames = null,
        bool contextual = false, bool includeClr = false, CancellationToken cancellationToken = default)
    {
        var values = await (await session.GetAsync(cancellationToken)).GetPropertiesAsync(new PropertiesRequest(nodeId, includeClr, contextual), cancellationToken);
        return propertyNames is null ? values : values.Where(value => propertyNames.Contains(value.Name, StringComparer.Ordinal)
            || propertyNames.Contains(value.Id, StringComparer.Ordinal)).ToArray();
    }

    [McpServerTool(Name = "set-prop"), Description("Apply a reversible diagnostic property override. Use the owner-qualified property ID to avoid ambiguous names. null clears this session's override. Existing bindings are preserved.")]
    public async Task<string> SetProperty(long nodeId, string propertyName, string? value, CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        var properties = await client.GetPropertiesAsync(nodeId, cancellationToken);
        var candidates = properties.Where(property => property.Id == propertyName || property.Name == propertyName).ToArray();
        if (candidates.Length != 1)
            throw new RpcException("ambiguous_property", "Supply one exact property ID from props.");
        if (value is null) await client.ClearPropertyAsync(nodeId, candidates[0].Id, cancellationToken);
        else await client.SetPropertyAsync(nodeId, candidates[0].Id, value, cancellationToken);
        return value is null ? "Override cleared" : "Diagnostic override applied";
    }

    [McpServerTool(Name = "styles", ReadOnly = true), Description("Inspect Avalonia 12 style frames, setters, binding expression descriptions and errors. Value equality is not proof that a setter uniquely wins.")]
    public async Task<StyleSnapshot> Styles(long nodeId, CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).GetStylesAsync(nodeId, cancellationToken);

    [McpServerTool(Name = "action"), Description("Focus, BringIntoView, Enable or Disable a target control. Enable/Disable use reversible diagnostic overrides.")]
    public async Task<ElementActionResult> Action(long nodeId, ElementAction action, CancellationToken cancellationToken) =>
        await (await session.GetAsync(cancellationToken)).PerformElementActionAsync(nodeId, action, cancellationToken);

    [McpServerTool(Name = "follow-focus"), Description("Follow keyboard focus in the target application through selection notifications. Disabling releases the mode for this session.")]
    public async Task<string> FollowFocus(bool enabled, CancellationToken cancellationToken)
    {
        await (await session.GetAsync(cancellationToken)).SetFollowFocusAsync(enabled, cancellationToken);
        return enabled ? "Focus following enabled" : "Focus following disabled";
    }

    [McpServerTool(Name = "pseudo-class"), Description("List pseudo-classes, temporarily set one, or reset this session's modification. Real input/state changes may recalculate pseudo-classes.")]
    public async Task<IReadOnlyList<PseudoClassInfo>> PseudoClass(long nodeId, string? name = null, bool? enabled = null, bool reset = false,
        CancellationToken cancellationToken = default)
    {
        var client = await session.GetAsync(cancellationToken);
        if (name is not null)
        {
            if (reset) await client.ResetPseudoClassAsync(nodeId, name, cancellationToken);
            else if (enabled is { } value) await client.SetPseudoClassAsync(nodeId, name, value, cancellationToken);
        }
        return await client.GetPseudoClassesAsync(nodeId, cancellationToken);
    }
}
