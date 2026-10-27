using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;

namespace AvaloniaDevTool.Cli;

public sealed class ToolSession : IAsyncDisposable
{
    private readonly DiagnosticsClient _client = new();
    private readonly SemaphoreSlim _attachment = new(1, 1);
    private string? _initialPipe;
    private readonly ApplicationDiscovery _discovery;
    private readonly NativeSessionOwner _native;
    private readonly CaptureCoordinator _capture;

    public ToolSession(string? initialPipeName)
    {
        _initialPipe = initialPipeName;
        _discovery = new(initialPipeName);
        _native = new(_client);
        _capture = new(_client, _attachment, async cancellation => await GetAsync(cancellation).ConfigureAwait(false));
    }

    public SessionInfo? CurrentSession => _client.Session;
    public bool IsConnected => _client.IsConnected;
    public long CurrentContentGeneration => _client.Preview.CurrentContentGeneration;
    internal string? NativeSessionIdentity => _native.NativeSessionIdentity;

    internal Task<NativeAutomation.NativeAutomationSession> GetNativeSessionAsync(CancellationToken token) =>
        _native.GetAsync(token);

    private Task ReleaseNativeAsync() => _native.ReleaseAsync();

    public (IReadOnlyList<ApplicationDescriptor> Applications, bool Truncated) DiscoverApplications() =>
        _discovery.DiscoverApplications();

    public void RememberApplication(string pipe, SessionInfo info) => _discovery.RememberApplication(pipe, info);

    private OwnedPreview? _preview;

    public async Task<PreviewAttachment> AttachProjectFileAsync(string xamlFilePath,
        string xamlProjectPath,
        bool build,
        CancellationToken cancellationToken)
    {
        var prepared = await ProjectPreviewService.PrepareAsync(xamlFilePath, xamlProjectPath, build, cancellationToken)
            .ConfigureAwait(false);
        return await AttachFileAsync(prepared.FilePath,
            prepared.AssemblyPath,
            prepared.ProjectPath,
            prepared.Headless,
            true,
            null,
            null,
            30,
            cancellationToken).ConfigureAwait(false);
    }

    internal Task<AgentCaptureResult> StartCaptureAsync(bool metrics,
        bool profiler,
        bool activity,
        bool logs,
        bool events,
        string eventName,
        string minimumLevel,
        string[]? meterNames,
        string[]? instrumentNames,
        string[]? profilerCategories,
        int capacity,
        int pollIntervalMs,
        CancellationToken cancellationToken) =>
        CapabilityErrors.AsMcpAsync(() => _capture.StartCaptureAsync(metrics,
            profiler,
            activity,
            logs,
            events,
            eventName,
            minimumLevel,
            meterNames,
            instrumentNames,
            profilerCategories,
            capacity,
            pollIntervalMs,
            cancellationToken));

    internal Task<AgentCaptureResult>
        ReadCaptureAsync(string captureId, bool stop, CancellationToken cancellationToken) =>
        CapabilityErrors.AsMcpAsync(() => _capture.ReadCaptureAsync(captureId, stop, cancellationToken));

    public async Task<DiagnosticsClient> GetAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
            return _client;
        await _attachment.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_client.IsConnected && _initialPipe is { } pipe)
            {
                await ConnectLocalAsync(pipe, cancellationToken).ConfigureAwait(false);
                _initialPipe = null;
            }

            if (!_client.IsConnected)
                throw new McpException("not_connected: Use attach-to-app or attach-to-file to select a target.");
            return _client;
        }
        finally
        {
            _attachment.Release();
        }
    }

    public async Task<SessionInfo> AttachAsync(string pipeName, CancellationToken cancellationToken)
    {
        await _attachment.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ConnectLocalAsync(pipeName, cancellationToken).ConfigureAwait(false);
            _initialPipe = null;
            if (_preview is { } previous && previous.PipeName != pipeName)
            {
                _preview = null;
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            return _client.Session!;
        }
        finally
        {
            _attachment.Release();
        }
    }

    private async Task ConnectLocalAsync(string pipeName, CancellationToken cancellationToken)
    {
        await ReleaseNativeAsync().ConfigureAwait(false);
        try
        {
            await _client.ConnectAsync(pipeName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException(
                "connection_timeout: The diagnostics endpoint did not respond within five seconds. Ensure the target is running, references AvaloniaDevTool.Agent, and calls DiagnosticsAgent.Attach after Avalonia startup; then list applications again.");
        }
        catch (IOException error)
        {
            throw new McpException(
                $"connection_failed: The diagnostics endpoint could not be opened. Ensure DiagnosticsAgent.Attach is running in the selected application, then list applications again. {error.Message}");
        }
    }

    public async Task<PreviewAttachment> AttachFileAsync(string filePath,
        string assemblyPath,
        string projectPath,
        bool headless,
        bool watch,
        string? applicationAssemblyPath,
        string? applicationType,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        applicationAssemblyPath ??= assemblyPath;
        foreach (var (path, name) in new[]
                 {
                     (filePath, "filePath"), (assemblyPath, "assemblyPath"), (projectPath, "projectPath"),
                     (applicationAssemblyPath, "applicationAssemblyPath")
                 })
        {
            if (!Path.IsPathFullyQualified(path))
                throw new McpException($"invalid_path: {name} must be an absolute file path.");
            if (!File.Exists(path))
                throw new McpException(
                    $"file_not_found: {name} does not exist: {path}. Build the selected application before attaching.");
        }

        if (timeoutSeconds is < 1 or > 120)
            throw new McpException("invalid_timeout: timeoutSeconds must be 1..120.");
        await _attachment.WaitAsync(cancellationToken).ConfigureAwait(false);
        OwnedPreview? candidate = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            candidate = await OwnedPreview.StartAsync(filePath,
                assemblyPath,
                projectPath,
                headless,
                watch,
                applicationAssemblyPath,
                applicationType,
                timeout.Token).ConfigureAwait(false);
            await ReleaseNativeAsync().ConfigureAwait(false);
            await _client.ConnectAsync(candidate.PipeName, timeout.Token).ConfigureAwait(false);
            var previous = _preview;
            _preview = candidate;
            candidate = null;
            _initialPipe = null;
            if (previous is not null)
                await previous.DisposeAsync().ConfigureAwait(false);
            return new PreviewAttachment(_client.Session!,
                Path.GetFullPath(filePath),
                Path.GetFullPath(assemblyPath),
                Path.GetFullPath(projectPath),
                _preview.PipeName,
                watch,
                headless,
                Path.GetFullPath(applicationAssemblyPath));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException(
                $"preview_timeout: The preview did not become ready within {timeoutSeconds} seconds; its owned process was stopped.");
        }
        finally
        {
            if (candidate is not null)
                await candidate.DisposeAsync().ConfigureAwait(false);
            _attachment.Release();
        }
    }

    public async Task DetachAsync()
    {
        await _attachment.WaitAsync().ConfigureAwait(false);
        try
        {
            _initialPipe = null;
            await ReleaseNativeAsync().ConfigureAwait(false);
            await _client.DisconnectAsync().ConfigureAwait(false);
            var preview = _preview;
            _preview = null;
            if (preview is not null)
                await preview.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _attachment.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DetachAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
        _capture.ClearTracking();
        _discovery.Clear();
    }
}
