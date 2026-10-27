using AvaloniaDevTool.Protocol;
using System.IO.Pipes;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class ProfilerClient
{
    private readonly DiagnosticsRpcContext context;

    internal ProfilerClient(DiagnosticsRpcContext context)
    {
        this.context = context;
        context.Subscribe((method, payload) =>
        {
            switch (method)
            {
                case ProfilerMethods.SamplesAdded:
                    ProfilerSamplesAdded?.Invoke(ProtocolJson.Read<ProfilerSamplesAddedNotification>(payload));
                    return true;
                default:
                    return false;
            }
        });
    }

    public event Action<ProfilerSamplesAddedNotification>? ProfilerSamplesAdded;

    public Task<ProfilerStatusDto> StartProfilerAsync(ProfilerStartRequest request,
        CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ProfilerStatusDto>(ProfilerMethods.Start, request, cancellationToken);

    public Task<ProfilerStatusDto> StopProfilerAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ProfilerStatusDto>(ProfilerMethods.Stop, null, cancellationToken);

    public Task<ProfilerStatusDto> ClearProfilerAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ProfilerStatusDto>(ProfilerMethods.Clear, null, cancellationToken);

    public Task<ProfilerStatusDto> GetProfilerStatusAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ProfilerStatusDto>(ProfilerMethods.Status, null, cancellationToken);

    public Task<ProfilerSnapshotDto> GetProfilerSnapshotAsync(CancellationToken cancellationToken = default) =>
        context.InvokeAsync<ProfilerSnapshotDto>(ProfilerMethods.Snapshot, null, cancellationToken);
}
