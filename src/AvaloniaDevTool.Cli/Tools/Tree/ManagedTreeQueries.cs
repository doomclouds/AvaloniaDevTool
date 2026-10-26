using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Cli;

internal static class ManagedTreeQueries
{
    internal static Task<TreeSearchResult> SearchAsync(ManagedToolContext context,
        string query,
        long scopeNodeId,
        bool searchTemplates,
        int limit,
        CancellationToken cancellationToken) =>
        context.WithClientAsync(cancellationToken,
            async (client, token) =>
            {
                var result = await client.SearchOfficialTreeAsync(
                        new OfficialTreeSearchRequest(query, context.NativeNodeId(scopeNodeId), searchTemplates, limit),
                        token)
                    .ConfigureAwait(false);
                return result with { Matches = context.PublicNodes(result.Matches) };
            });
}
