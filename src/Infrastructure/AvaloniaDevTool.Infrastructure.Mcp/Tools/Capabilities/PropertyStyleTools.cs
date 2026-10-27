using AvaloniaDevTool.Client;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ModelContextProtocol;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace AvaloniaDevTool.Cli;

[McpServerToolType]
public sealed class PropertyStyleTools(ToolSession session, ManagedToolContext context)
    : ManagedToolBase(session, context)
{
    [McpServerTool(Name = "props", ReadOnly = true, Destructive = false),
     Description("Gets property values for a node.")]
    public Task<IReadOnlyList<PropertyInfoDto>> Props(
        [Description("Node ID.")] long nodeId,
        [Description("Names to filter. Omit for all.")]
        string?[]? propertyNames = null,
        bool contextual = false,
        bool includeClr = false,
        CancellationToken cancellationToken = default) =>
        ManagedPropertyQueries.ReadAsync(ManagedContext,
            nodeId,
            propertyNames,
            cancellationToken,
            contextual,
            includeClr);

    [McpServerTool(Name = "set-prop", ReadOnly = false, Destructive = true),
     Description("Sets a property value. Use 'null' or 'unset' for special values.")]
    public Task<string> SetProperty(
        [Description("Node ID.")] long nodeId,
        [Description("Property name.")] string propertyName,
        [Description("Value.")] string value,
        [Description("If value is XAML (with xmlns)")]
        bool isXamlValue = false,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(cancellationToken,
            async (client, token) =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
                if (value is null)
                    throw new McpException("invalid_argument: value must be a string; use the literal 'null'.");
                var nativeId = NativeNodeId(nodeId);
                var properties = await client.Properties.GetPropertiesAsync(nativeId, token).ConfigureAwait(false);
                var candidates = PropertyCandidates(properties, propertyName);
                if (candidates.Length != 1)
                    throw new McpException(candidates.Length == 0
                        ? "property_not_found: Supply a property name or ID from props."
                        : "ambiguous_property: Supply one owner-qualified property ID from props.");
                await client
                    .Properties.SetOfficialPropertyAsync(
                        new SetPropertyValueRequest(nativeId, candidates[0].Id, value, isXamlValue),
                        token)
                    .ConfigureAwait(false);
                return !isXamlValue && value == "unset" ? "Diagnostic override cleared" : "Diagnostic override applied";
            });

    private static PropertyInfoDto[] PropertyCandidates(IReadOnlyList<PropertyInfoDto> properties, string name) =>
        ManagedPropertyQueries.Candidates(properties, name);

    [McpServerTool(Name = "styles", ReadOnly = true, Destructive = false),
     Description("Gets applied styles and setters for a node.")]
    public Task<StyleSnapshot> Styles(
        [Description("Node ID.")] long nodeId,
        [Description("Property names. Omit for all.")]
        string?[]? propertyNames = null,
        [Description("Include default values.")]
        bool includeDefaults = false,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(cancellationToken,
            async (client, token) =>
            {
                var result = await client
                    .Properties.GetOfficialStylesAsync(
                        new InspectStylesRequest(NativeNodeId(nodeId), propertyNames, includeDefaults),
                        token)
                    .ConfigureAwait(false);
                return result with
                {
                    Bindings = result.Bindings.Select(binding => binding with
                    {
                        Details = binding.Details is { } details ? PublicPropertyBindingDetails(details) : null
                    }).ToArray()
                };
            });

    [McpServerTool(Name = "pseudo-class", ReadOnly = false, Destructive = true),
     Description("Sets pseudo-class. Omit pseudoClass to list available.")]
    public Task<IReadOnlyList<PseudoClassInfo>> PseudoClass(
        [Description("Node ID.")] long nodeId,
        [Description("Pseudo-class, examples: pointerover, pressed, focus, disabled, selected.")]
        string? pseudoClass = null,
        [Description("Activate or deactivate.")]
        bool? isActive = null,
        bool reset = false,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(cancellationToken,
            async (client, token) =>
            {
                var nativeId = NativeNodeId(nodeId);
                var normalized = pseudoClass is null ? null :
                    pseudoClass.StartsWith(':') ? pseudoClass : ":" + pseudoClass;
                if (pseudoClass is not null && reset)
                    await client.Styles.ResetPseudoClassAsync(nativeId, normalized!, token).ConfigureAwait(false);
                else if (pseudoClass is not null && isActive is { } active)
                {
                    await client.Styles.SetPseudoClassAsync(nativeId, normalized!, active, token).ConfigureAwait(false);
                }

                return await client.Styles.GetPseudoClassesAsync(nativeId, token).ConfigureAwait(false);
            });

    [McpServerTool(Name = "action", ReadOnly = false, Destructive = true),
     Description("Performs an action on a UI element.")]
    public Task<ElementActionResult> Action(
        [Description("The NodeId.")] long nodeId,
        [Description("The action to perform on the node.")]
        ElementControlAction action,
        CancellationToken cancellationToken = default) =>
        WithClientAsync(cancellationToken,
            async (client, token) =>
            {
                var nativeAction = action switch
                {
                    ElementControlAction.Focus => ElementAction.Focus,
                    ElementControlAction.Enable => ElementAction.Enable,
                    ElementControlAction.Disable => ElementAction.Disable,
                    ElementControlAction.BringIntoView => ElementAction.BringIntoView,
                    _ => throw new McpException("invalid_argument: The requested element action is unsupported.")
                };
                return await client.Input.PerformElementActionAsync(NativeNodeId(nodeId), nativeAction, token)
                    .ConfigureAwait(false);
            });

    private BindingDetailValue PublicPropertyBindingValue(BindingDetailValue value) =>
        value with { ObjectId = PublicNodeId(value.ObjectId) };

    private BindingDetailValue? PublicOptionalPropertyBindingValue(BindingDetailValue? value) =>
        value is null ? null : PublicPropertyBindingValue(value);

    private BindingDetails PublicPropertyBindingDetails(BindingDetails details) => details with
    {
        SourceType = PublicPropertyBindingValue(details.SourceType),
        Source = PublicPropertyBindingValue(details.Source),
        Path = PublicPropertyBindingValue(details.Path), Mode = PublicPropertyBindingValue(details.Mode),
        Converter = PublicPropertyBindingValue(details.Converter),
        ConverterParameter = PublicPropertyBindingValue(details.ConverterParameter),
        FallbackValue = PublicPropertyBindingValue(details.FallbackValue),
        TargetNullValue = PublicPropertyBindingValue(details.TargetNullValue),
        UpdateSourceTrigger = PublicPropertyBindingValue(details.UpdateSourceTrigger),
        StringFormat = PublicPropertyBindingValue(details.StringFormat),
        ElementName = PublicPropertyBindingValue(details.ElementName),
        RelativeSource = PublicPropertyBindingValue(details.RelativeSource),
        Children = details.Children.Select(PublicPropertyBindingDetails).ToArray(),
        DataContext = PublicOptionalPropertyBindingValue(details.DataContext),
        DataContextOrigin = PublicOptionalPropertyBindingValue(details.DataContextOrigin),
        CurrentValue = PublicOptionalPropertyBindingValue(details.CurrentValue),
        ResourceKey = PublicOptionalPropertyBindingValue(details.ResourceKey),
        ConverterCulture = PublicOptionalPropertyBindingValue(details.ConverterCulture),
        ConverterCultureOrigin = PublicOptionalPropertyBindingValue(details.ConverterCultureOrigin)
    };
}
