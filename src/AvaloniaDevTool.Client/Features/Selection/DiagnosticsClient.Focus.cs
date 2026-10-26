using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public async Task SetFollowFocusAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(ExplorationMethods.FollowFocus,
            new FollowFocusRequest(enabled),
            cancellationToken).ConfigureAwait(false);
}
