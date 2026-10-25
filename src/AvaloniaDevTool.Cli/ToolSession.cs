using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;

namespace AvaloniaDevTool.Cli;

public sealed partial class ToolSession(string? initialPipeName) : IAsyncDisposable
{
    private readonly DiagnosticsClient _client = new();
    private readonly SemaphoreSlim _attachment = new(1, 1);
    private string? _initialPipe = initialPipeName;
    private readonly string? _configuredPipe = initialPipeName;
    private OwnedPreview? _preview;

    public async Task<DiagnosticsClient> GetAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected) return _client;
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
        finally { _attachment.Release(); }
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
        finally { _attachment.Release(); }
    }

    private async Task ConnectLocalAsync(string pipeName, CancellationToken cancellationToken)
    {
        await ReleaseNativeAsync().ConfigureAwait(false);
        try
        {
            await _client.ConnectAsync(pipeName, cancellationToken).ConfigureAwait(false);
            _transportIsRemote = false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException("connection_timeout: The diagnostics endpoint did not respond within five seconds. Ensure the target is running, references AvaloniaDevTool.Agent, and calls DiagnosticsAgent.Attach after Avalonia startup; then list applications again.");
        }
        catch (IOException error)
        {
            throw new McpException($"connection_failed: The diagnostics endpoint could not be opened. Ensure DiagnosticsAgent.Attach is running in the selected application, then list applications again. {error.Message}");
        }
    }

    public async Task<PreviewAttachment> AttachFileAsync(string filePath, string assemblyPath, string projectPath,
        bool headless, bool watch, string? applicationAssemblyPath, string? applicationType, int timeoutSeconds, CancellationToken cancellationToken)
    {
        applicationAssemblyPath ??= assemblyPath;
        foreach (var (path, name) in new[] { (filePath, "filePath"), (assemblyPath, "assemblyPath"), (projectPath, "projectPath"), (applicationAssemblyPath, "applicationAssemblyPath") })
        {
            if (!Path.IsPathFullyQualified(path)) throw new McpException($"invalid_path: {name} must be an absolute file path.");
            if (!File.Exists(path)) throw new McpException($"file_not_found: {name} does not exist: {path}. Build the selected application before attaching.");
        }
        if (timeoutSeconds is < 1 or > 120) throw new McpException("invalid_timeout: timeoutSeconds must be 1..120.");
        await _attachment.WaitAsync(cancellationToken).ConfigureAwait(false);
        OwnedPreview? candidate = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            candidate = await OwnedPreview.StartAsync(filePath, assemblyPath, projectPath, headless, watch, applicationAssemblyPath, applicationType, timeout.Token).ConfigureAwait(false);
            await ReleaseNativeAsync().ConfigureAwait(false);
            await _client.ConnectAsync(candidate.PipeName, timeout.Token).ConfigureAwait(false);
            _transportIsRemote = false;
            var previous = _preview;
            _preview = candidate;
            candidate = null;
            _initialPipe = null;
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
            return new PreviewAttachment(_client.Session!, Path.GetFullPath(filePath), Path.GetFullPath(assemblyPath),
                Path.GetFullPath(projectPath), _preview.PipeName, watch, headless, Path.GetFullPath(applicationAssemblyPath));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new McpException($"preview_timeout: The preview did not become ready within {timeoutSeconds} seconds; its owned process was stopped.");
        }
        finally
        {
            if (candidate is not null) await candidate.DisposeAsync().ConfigureAwait(false);
            _attachment.Release();
        }
    }

    public async Task<SessionInfo> AttachRemoteAsync(string endpoint, string tokenEnvironmentVariable, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var address) || address.Scheme is not ("ws" or "wss"))
            throw new McpException("invalid_endpoint: Specify an absolute ws or wss diagnostics address.");
        if (tokenEnvironmentVariable.Length is < 1 or > 128 ||
            !(char.IsAsciiLetter(tokenEnvironmentVariable[0]) || tokenEnvironmentVariable[0] == '_') ||
            tokenEnvironmentVariable.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new McpException("invalid_token_environment: Specify an environment variable name, not the token value.");
        var token = Environment.GetEnvironmentVariable(tokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token))
            throw new McpException("remote_token_missing: The named environment variable has no token.");
        await _attachment.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReleaseNativeAsync().ConfigureAwait(false);
            await _client.ConnectRemoteAsync(address, token, cancellationToken).ConfigureAwait(false);
            _transportIsRemote = true;
            _initialPipe = null;
            var previous = _preview;
            _preview = null;
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
            return _client.Session!;
        }
        finally { _attachment.Release(); }
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
            if (preview is not null) await preview.DisposeAsync().ConfigureAwait(false);
        }
        finally { _attachment.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await DetachAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed record PreviewAttachment(SessionInfo Session, string FilePath, string AssemblyPath, string ProjectPath,
    string PipeName, bool Watching, bool Headless, string ApplicationAssemblyPath);

internal sealed class OwnedPreview : IAsyncDisposable
{
    private readonly Process _process;
    private readonly NamedPipeServerStream _control;
    private readonly Task _stdout, _stderr;
    private readonly CancellationTokenSource _drainStop = new();
    private readonly StringBuilder _diagnostics = new();
    public string PipeName { get; }

    private OwnedPreview(Process process, NamedPipeServerStream control, string pipeName)
    {
        _process = process;
        _control = control;
        PipeName = pipeName;
        _stdout = DrainAsync(process.StandardOutput);
        _stderr = DrainAsync(process.StandardError);
    }

    public static async Task<OwnedPreview> StartAsync(string file, string assembly, string project, bool headless,
        bool watch, string applicationAssemblyPath, string? applicationType, CancellationToken token)
    {
        var previewer = Path.Combine(AppContext.BaseDirectory, "previewer", "AvaloniaDevTool.Previewer.dll");
        if (!File.Exists(previewer)) throw new McpException($"previewer_not_found: The CLI distribution must include {previewer}.");
        var pipe = DiagnosticsProtocol.PipePrefix + "preview-" + Guid.NewGuid().ToString("N");
        var controlName = "avdt-preview-control-" + Guid.NewGuid().ToString("N");
        var control = new NamedPipeServerStream(controlName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(project)!
        };
        foreach (var argument in new[] { previewer, "--xaml", file, "--assembly", assembly, "--project", project,
            "--pipe", pipe, "--control-pipe", controlName, "--application-assembly", applicationAssemblyPath }) start.ArgumentList.Add(argument);
        if (headless) start.ArgumentList.Add("--headless");
        if (!watch) start.ArgumentList.Add("--no-watch");
        if (applicationType is not null) { start.ArgumentList.Add("--application-type"); start.ArgumentList.Add(applicationType); }
        Process? process = null;
        OwnedPreview? owned = null;
        try
        {
            token.ThrowIfCancellationRequested();
            process = Process.Start(start) ?? throw new McpException("preview_start_failed: Could not start the preview process.");
            owned = new OwnedPreview(process, control, pipe);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            var connected = control.WaitForConnectionAsync(startup.Token);
            var exited = process.WaitForExitAsync(startup.Token);
            var first = await Task.WhenAny(connected, exited).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (first == exited && !control.IsConnected)
                throw new McpException("preview_start_failed: " + owned.Diagnostics());
            await connected.ConfigureAwait(false);
            var bytes = new List<byte>();
            var buffer = new byte[1];
            while (bytes.Count < 8192)
            {
                var count = await control.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) throw new McpException("preview_start_failed: The preview exited before its readiness handshake. " + owned.Diagnostics());
                if (buffer[0] == (byte)'\n') break;
                bytes.Add(buffer[0]);
            }
            if (bytes.Count >= 8192) throw new McpException("invalid_preview_handshake: The readiness response exceeded 8 KiB.");
            var ready = JsonSerializer.Deserialize<Startup>(Encoding.UTF8.GetString(bytes.ToArray()));
            if (ready is not { Ready: true } || ready.ProcessId != process.Id || ready.PipeName != pipe)
                throw new McpException("preview_start_failed: " + (ready?.Error ?? "Invalid readiness response."));
            startup.Cancel();
            return owned;
        }
        catch
        {
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
            else { process?.Dispose(); control.Dispose(); }
            throw;
        }
    }

    private async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), _drainStop.Token).ConfigureAwait(false) is var count && count > 0)
                lock (_diagnostics)
                {
                    if (_diagnostics.Length + count > 8192) _diagnostics.Remove(0, Math.Min(_diagnostics.Length, count));
                    _diagnostics.Append(buffer, 0, count);
                }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private string Diagnostics() { lock (_diagnostics) return _diagnostics.ToString(); }

    public async ValueTask DisposeAsync()
    {
        await _control.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (!_process.HasExited)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    try { _process.Kill(entireProcessTree: true); }
                    catch (System.ComponentModel.Win32Exception) when (_process.HasExited) { }
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            _drainStop.Cancel();
            await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }
        finally { _drainStop.Dispose(); _process.Dispose(); }
    }

    private sealed record Startup(bool Ready, int ProcessId, string PipeName, string? Error);
}

