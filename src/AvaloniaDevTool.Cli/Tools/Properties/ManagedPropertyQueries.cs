using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Cli;

internal static class ManagedPropertyQueries
{
    internal static Task<IReadOnlyList<PropertyInfoDto>> ReadAsync(ManagedToolContext context,
        long nodeId,
        string?[]? propertyNames,
        CancellationToken cancellationToken) =>
        context.WithClientAsync<IReadOnlyList<PropertyInfoDto>>(cancellationToken,
            async (client, token) =>
            {
                var properties = await client.GetPropertiesAsync(context.NativeNodeId(nodeId), token)
                    .ConfigureAwait(false);
                var included = propertyNames?.OfType<string>().SelectMany(name => Candidates(properties, name))
                    .Select(property => property.Id).ToHashSet(StringComparer.Ordinal);
                return properties.Where(property => included is null || included.Contains(property.Id))
                    .Select(property => property with
                    {
                        Presentation = property.Presentation is { } presentation
                            ? presentation with { ValueId = context.PublicNodeId(presentation.ValueId) }
                            : null
                    }).ToArray();
            });

    internal static PropertyInfoDto[] Candidates(IReadOnlyList<PropertyInfoDto> properties, string name)
    {
        var exact = properties.Where(property => property.Id == name).ToArray();
        if (exact.Length > 0)
            return exact;
        var named = properties.Where(property => property.Name == name).ToArray();
        var wrappers = named.Where(property => property.HasClrWrapper).ToArray();
        return wrappers.Length == 1 ? wrappers : named;
    }
}
