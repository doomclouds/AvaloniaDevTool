using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client.Attachment;

public static class WindowsAgentAttachment
{
    public static async Task<ProcessAttachmentInfo> EnsureAsync(int processId, DateTimeOffset startTime,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("unsupported_platform: External attachment currently requires Windows x64.");
        var total = Stopwatch.StartNew();
        using var process = Process.GetProcessById(processId);
        if (process.StartTime.ToUniversalTime() != startTime.UtcDateTime)
            throw new InvalidOperationException("stale_process: Discover applications again; this PID was reused.");
        var runtime = process.Modules.Cast<ProcessModule>().FirstOrDefault(module =>
            module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
        if (runtime?.FileVersionInfo.FileMajorPart != 10)
            throw new PlatformNotSupportedException("unsupported_runtime: The current Agent requires a .NET 10 CoreCLR target.");
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaloniaDevTool", "attachment");
        Directory.CreateDirectory(cache);
        await using var gate = await LockAsync(Path.Combine(cache, $"{processId}-{startTime.UtcTicks}.lock"), cancellationToken);
        var pipeName = DiagnosticsProtocol.PipePrefix + processId;
        if (Directory.EnumerateFiles(@"\\.\pipe\").Any(path => Path.GetFileName(path) == pipeName))
            return new("existing-endpoint", runtime.FileVersionInfo.FileVersion ?? "unknown", "unknown", 0, 0, total.Elapsed.TotalMilliseconds, true);

        var runtimeRoot = Environment.GetEnvironmentVariable("AVDT_RUNTIME_ROOT") is { Length: > 0 } root
            ? Path.GetFullPath(root) : AppContext.BaseDirectory;
        var nativeDirectory = Path.Combine(runtimeRoot, "attachment");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(nativeDirectory, "manifest.json"), cancellationToken));
        var native = Path.Combine(nativeDirectory, "NativeBootstrap.dll");
        var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(native, cancellationToken))).ToLowerInvariant();
        if (actualHash != manifest.RootElement.GetProperty("sha256").GetString())
            throw new InvalidOperationException("payload_corrupt: The native bootstrap SHA-256 does not match its manifest.");
        var bootstrap = Path.Combine(nativeDirectory, "AvaloniaDevTool.Bootstrap.Pipe.dll");
        if (!File.Exists(bootstrap))
            throw new InvalidOperationException("payload_missing: The bundled managed bootstrap is missing.");
        var injection = Stopwatch.StartNew();
        using var probe = await InvokeAsync("probe", null);
        injection.Stop();
        var avalonia = probe.RootElement.GetProperty("avaloniaVersion").GetString()
            ?? throw new InvalidOperationException("unsupported_target: This process has no loaded Avalonia application.");
        if (avalonia is not ("12.1.1" or "12.1.4"))
            throw new PlatformNotSupportedException($"unsupported_avalonia: Avalonia {avalonia} has no verified Agent build; supported versions are 12.1.1 and 12.1.4.");
        var preparation = Stopwatch.StartNew();
        var agent = Path.Combine(nativeDirectory, "agents", avalonia, "AvaloniaDevTool.Agent.dll");
        if (!File.Exists(agent))
            throw new InvalidOperationException($"payload_missing: The bundled Agent for Avalonia {avalonia} is missing.");
        preparation.Stop();
        injection.Start();
        using var started = await InvokeAsync("start", agent);
        injection.Stop();
        if (started.RootElement.GetProperty("pipeName").GetString() != pipeName)
            throw new InvalidOperationException("endpoint_mismatch: The bootstrap returned an unexpected diagnostics endpoint.");
        return new("windows-native-coreclr", runtime.FileVersionInfo.FileVersion ?? "unknown", avalonia,
            preparation.Elapsed.TotalMilliseconds, injection.Elapsed.TotalMilliseconds, total.Elapsed.TotalMilliseconds, false);

        async Task<JsonDocument> InvokeAsync(string action, string? agentPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var replyName = "avdt-bootstrap-" + Guid.NewGuid().ToString("N");
            await using var reply = new NamedPipeServerStream(replyName, PipeDirection.In, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var request = JsonSerializer.Serialize(new
            {
                Action = action, ReplyPipeName = replyName, AgentPath = agentPath
            });
            // Once native execution starts, cancellation does not imply that target work was undone.
            WindowsRuntimeInjector.Invoke(processId, startTime, native, bootstrap, request);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            JsonDocument result;
            try
            {
                await reply.WaitForConnectionAsync(timeout.Token);
                using var reader = new StreamReader(reply, Encoding.UTF8);
                var json = await reader.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException("The bootstrap closed without a reply.");
                result = JsonDocument.Parse(json);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException)
            {
                throw new TimeoutException($"attach_outcome_unknown: Bootstrap {action} completion was not confirmed; do not reinject automatically.", error);
            }
            if (result.RootElement.GetProperty("processId").GetInt32() != processId)
            {
                result.Dispose();
                throw new InvalidOperationException("process_mismatch: The bootstrap responded for another process.");
            }
            var state = result.RootElement.GetProperty("state").GetString();
            if (state == (action == "probe" ? "probed" : "started"))
                return result;
            var message = result.RootElement.TryGetProperty("error", out var detail) ? detail.GetString() : state;
            result.Dispose();
            throw new InvalidOperationException($"bootstrap_failed: {action}: {message}");
        }
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(100, token); }
        }
    }
}
