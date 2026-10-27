using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using ModelContextProtocol;

namespace AvaloniaDevTool.Cli;

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

    public static async Task<OwnedPreview> StartAsync(string file,
        string assembly,
        string project,
        bool headless,
        bool watch,
        string applicationAssemblyPath,
        string? applicationType,
        CancellationToken token)
    {
        var previewer = Hosting.RuntimeAssets.Resolve("previewer", "AvaloniaDevTool.Previewer");
        if (!File.Exists(previewer))
            throw new McpException($"previewer_not_found: The CLI distribution must include {previewer}.");
        var pipe = DiagnosticsProtocol.PipePrefix + "preview-" + Guid.NewGuid().ToString("N");
        var controlName = "avdt-preview-control-" + Guid.NewGuid().ToString("N");
        var control = new NamedPipeServerStream(controlName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = Hosting.RuntimeAssets.StartInfo(previewer);
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.WorkingDirectory = Path.GetDirectoryName(project)!;
        foreach (var argument in new[]
                 {
                     "--xaml", file, "--assembly", assembly, "--project", project,
                     "--pipe", pipe, "--control-pipe", controlName, "--application-assembly", applicationAssemblyPath
                 })
            start.ArgumentList.Add(argument);
        if (headless)
            start.ArgumentList.Add("--headless");
        if (!watch)
            start.ArgumentList.Add("--no-watch");
        if (applicationType is not null)
        {
            start.ArgumentList.Add("--application-type");
            start.ArgumentList.Add(applicationType);
        }

        Process? process = null;
        OwnedPreview? owned = null;
        try
        {
            token.ThrowIfCancellationRequested();
            process = Process.Start(start) ??
                      throw new McpException("preview_start_failed: Could not start the preview process.");
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
                if (count == 0)
                    throw new McpException("preview_start_failed: The preview exited before its readiness handshake. " +
                                           owned.Diagnostics());
                if (buffer[0] == (byte)'\n')
                    break;
                bytes.Add(buffer[0]);
            }

            if (bytes.Count >= 8192)
                throw new McpException("invalid_preview_handshake: The readiness response exceeded 8 KiB.");
            var ready = JsonSerializer.Deserialize<Startup>(Encoding.UTF8.GetString(bytes.ToArray()));
            if (ready is not { Ready: true } || ready.ProcessId != process.Id || ready.PipeName != pipe)
                throw new McpException("preview_start_failed: " + (ready?.Error ?? "Invalid readiness response."));
            startup.Cancel();
            return owned;
        }
        catch
        {
            if (owned is not null)
                await owned.DisposeAsync().ConfigureAwait(false);
            else
            {
                process?.Dispose();
                control.Dispose();
            }

            throw;
        }
    }

    private async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), _drainStop.Token).ConfigureAwait(false) is var count &&
                   count > 0)
                lock (_diagnostics)
                {
                    if (_diagnostics.Length + count > 8192)
                        _diagnostics.Remove(0, Math.Min(_diagnostics.Length, count));
                    _diagnostics.Append(buffer, 0, count);
                }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
                                              or OperationCanceledException)
        {
        }
    }

    private string Diagnostics()
    {
        lock (_diagnostics)
            return _diagnostics.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        await _control.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (!_process.HasExited)
            {
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    catch (System.ComponentModel.Win32Exception) when (_process.HasExited)
                    {
                    }

                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }

            _drainStop.Cancel();
            await Task.WhenAll(_stdout, _stderr).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _drainStop.Dispose();
            _process.Dispose();
        }
    }

    private sealed record Startup(bool Ready, int ProcessId, string PipeName, string? Error);
}
