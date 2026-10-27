using AvaloniaDevTool.Client;
using System.Diagnostics;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;

namespace AvaloniaDevTool.Cli;

internal sealed class NativeSessionOwner(DiagnosticsClient client)
{
    private readonly SemaphoreSlim _nativeGate = new(1, 1);
    private NativeAutomationSession? _nativeSession;
    private SessionInfo? _nativeOwner;

    internal string? NativeSessionIdentity => _nativeSession?.SessionId;

    internal async Task<NativeAutomationSession> GetAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new McpException("unsupported_platform: Native desktop automation currently requires Windows.");
        await _nativeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var owner = client.Session;
            if (!client.IsConnected || owner is null)
            {
                await ReleaseNativeCoreAsync().ConfigureAwait(false);
                throw new McpException("not_connected: Attach to a local application before using native tools.");
            }

            if (_nativeSession is { IsAlive: true } && ReferenceEquals(owner, _nativeOwner))
                return _nativeSession;
            await ReleaseNativeCoreAsync().ConfigureAwait(false);
            DateTimeOffset start;
            try
            {
                using var process = Process.GetProcessById(owner.ProcessId);
                start = new DateTimeOffset(process.StartTime.ToUniversalTime());
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
            {
                throw new McpException(
                    $"target_unavailable: The attached local process identity could not be verified. {error.Message}");
            }

            var candidate = await NativeAutomationSession.AttachAsync(owner.ProcessId,
                    start,
                    new NativeAutomationOptions
                    {
                        WorkerPath = Hosting.RuntimeAssets.Resolve("native-worker",
                            "AvaloniaDevTool.NativeAutomation.Worker")
                    },
                    cancellationToken: token)
                .ConfigureAwait(false);
            if (!ReferenceEquals(owner, client.Session) || !client.IsConnected)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
                throw new McpException(
                    "stale_session: The attached application changed while native automation was starting.");
            }

            _nativeOwner = owner;
            return _nativeSession = candidate;
        }
        finally
        {
            _nativeGate.Release();
        }
    }

    internal async Task ReleaseAsync()
    {
        await _nativeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ReleaseNativeCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _nativeGate.Release();
        }
    }

    private async Task ReleaseNativeCoreAsync()
    {
        var previous = _nativeSession;
        _nativeSession = null;
        _nativeOwner = null;
        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(false);
    }
}
