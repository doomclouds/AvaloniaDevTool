using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public event Action<ActivityEntry>? ActivityEntryAdded;

    public event Action<ActivityStatus>? ActivityStateChanged;

    public async Task StartActivityAsync(ActivityStartRequest request, CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(ActivityMethods.Start, request, cancellationToken).ConfigureAwait(false);

    public async Task StopActivityAsync(CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(ActivityMethods.Stop, null, cancellationToken).ConfigureAwait(false);

    public async Task ClearActivityAsync(CancellationToken cancellationToken = default) =>
        _ = await InvokeAsync<OperationResult>(ActivityMethods.Clear, null, cancellationToken).ConfigureAwait(false);

    public Task<IReadOnlyList<ActivityEntry>> GetActivityAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<IReadOnlyList<ActivityEntry>>(ActivityMethods.Snapshot, null, cancellationToken);

    public Task<ActivityStatus> GetActivityStatusAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<ActivityStatus>(ActivityMethods.Status, null, cancellationToken);
}
