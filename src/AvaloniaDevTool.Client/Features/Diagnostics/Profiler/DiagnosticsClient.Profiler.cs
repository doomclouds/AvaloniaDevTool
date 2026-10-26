using System.Text.Json;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    public event Action<ProfilerSamplesAddedNotification>? ProfilerSamplesAdded;

    public Task<ProfilerStatusDto> StartProfilerAsync(ProfilerStartRequest request,
        CancellationToken cancellationToken = default) =>
        InvokeAsync<ProfilerStatusDto>(ProfilerMethods.Start, request, cancellationToken);

    public Task<ProfilerStatusDto> StopProfilerAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<ProfilerStatusDto>(ProfilerMethods.Stop, null, cancellationToken);

    public Task<ProfilerStatusDto> ClearProfilerAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<ProfilerStatusDto>(ProfilerMethods.Clear, null, cancellationToken);

    public Task<ProfilerStatusDto> GetProfilerStatusAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<ProfilerStatusDto>(ProfilerMethods.Status, null, cancellationToken);

    public Task<ProfilerSnapshotDto> GetProfilerSnapshotAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync<ProfilerSnapshotDto>(ProfilerMethods.Snapshot, null, cancellationToken);

    private void HandleProfilerSamplesAdded(JsonElement payload) =>
        ProfilerSamplesAdded?.Invoke(ProtocolJson.Read<ProfilerSamplesAddedNotification>(payload));
}
