using System.Diagnostics;
using AvaloniaDevTool.NativeAutomation;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;

namespace AvaloniaDevTool.Cli;

public sealed partial class ToolSession
{
    private readonly SemaphoreSlim _nativeGate = new(1, 1);
    private NativeAutomationSession? _nativeSession;
    private SessionInfo? _nativeOwner;
    private bool _transportIsRemote;

    internal async Task<NativeAutomationSession> GetNativeSessionAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new McpException("unsupported_platform: Native desktop automation currently requires Windows.");
        await _nativeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var owner = _client.Session;
            if (!_client.IsConnected || owner is null)
            {
                await ReleaseNativeCoreAsync().ConfigureAwait(false);
                throw new McpException("not_connected: Attach to a local application before using native tools.");
            }

            if (_transportIsRemote)
                throw new McpException(
                    "unsupported_transport: Native automation cannot control a remote application's process on this computer.");
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

            var candidate = await NativeAutomationSession.AttachAsync(owner.ProcessId, start, cancellationToken: token)
                .ConfigureAwait(false);
            if (!ReferenceEquals(owner, _client.Session) || !_client.IsConnected)
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

    private async Task ReleaseNativeAsync()
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
