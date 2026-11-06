using System.Diagnostics;
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

        var sourceRoot = Environment.GetEnvironmentVariable("AVDT_SOURCE_ROOT");
        if (string.IsNullOrEmpty(sourceRoot) || !File.Exists(Path.Combine(sourceRoot, "Directory.Build.props")))
            throw new InvalidOperationException("source_runtime_required: Start the MCP with dotnet run --file AvaloniaDevTool.cs after Prepare.ps1.");
        sourceRoot = Path.GetFullPath(sourceRoot);
        var nativeDirectory = Path.Combine(sourceRoot, "native", "win-x64");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(nativeDirectory, "manifest.json"), cancellationToken));
        var native = Path.Combine(nativeDirectory, "NativeBootstrap.dll");
        var actualHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(native, cancellationToken))).ToLowerInvariant();
        if (actualHash != manifest.RootElement.GetProperty("sha256").GetString())
            throw new InvalidOperationException("payload_corrupt: The native bootstrap SHA-256 does not match its manifest.");
        var bootstrap = Path.Combine(sourceRoot, "src", "Hosts", "AvaloniaDevTool.Bootstrap", "bin", "Release", "net10.0", "AvaloniaDevTool.Bootstrap.dll");
        if (!File.Exists(bootstrap))
            throw new InvalidOperationException("setup_required: Run Prepare.ps1 to build the runtime-only bootstrap.");
        var run = Path.Combine(cache, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var injection = Stopwatch.StartNew();
        using var probe = await InvokeAsync("probe", null);
        injection.Stop();
        var avalonia = probe.RootElement.GetProperty("avaloniaVersion").GetString()
            ?? throw new InvalidOperationException("unsupported_target: This process has no loaded Avalonia application.");
        if (avalonia is not ("12.1.1" or "12.1.4"))
            throw new PlatformNotSupportedException($"unsupported_avalonia: Avalonia {avalonia} has no verified Agent build; supported versions are 12.1.1 and 12.1.4.");
        var preparation = Stopwatch.StartNew();
        var agent = await BuildAgentAsync(sourceRoot, cache, avalonia, cancellationToken);
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
            var requestPath = Path.Combine(run, action + ".request.json");
            var statusPath = Path.Combine(run, action + ".status.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
            {
                Action = action, StatusPath = statusPath, AgentPath = agentPath
            }), cancellationToken);
            // Once native execution starts, cancellation does not imply that target work was undone.
            WindowsRuntimeInjector.Invoke(processId, startTime, native, bootstrap, requestPath);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(statusPath))
                {
                    var result = JsonDocument.Parse(await File.ReadAllTextAsync(statusPath, CancellationToken.None));
                    var state = result.RootElement.GetProperty("state").GetString();
                    if (result.RootElement.GetProperty("processId").GetInt32() != processId)
                    {
                        result.Dispose();
                        throw new InvalidOperationException("process_mismatch: The bootstrap responded for another process.");
                    }
                    if (state is "probed" or "started")
                        return result;
                    var message = result.RootElement.TryGetProperty("error", out var error) ? error.GetString() : state;
                    result.Dispose();
                    throw new InvalidOperationException("bootstrap_failed: " + message);
                }
                await Task.Delay(50, CancellationToken.None);
            }
            throw new TimeoutException("attach_outcome_unknown: Bootstrap completion was not confirmed; do not reinject automatically.");
        }
    }

    private static async Task<string> BuildAgentAsync(string sourceRoot, string cache, string avalonia, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var inputs = Directory.EnumerateFiles(Path.Combine(sourceRoot, "src"), "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) &&
                           !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                           Path.GetExtension(path) is ".cs" or ".csproj" or ".props" or ".targets")
            .Append(Path.Combine(sourceRoot, "Directory.Build.props")).Order(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(sourceRoot, input)));
            hash.AppendData(await File.ReadAllBytesAsync(input, token));
        }
        var fingerprint = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()[..24];
        var output = Path.Combine(cache, "agents", fingerprint, avalonia);
        Directory.CreateDirectory(output);
        await using var gate = await LockAsync(Path.Combine(output, "build.lock"), token);
        var agent = Path.Combine(output, "bin", "AvaloniaDevTool.Agent", "release", "AvaloniaDevTool.Agent.dll");
        var ready = Path.Combine(output, "ready");
        if (File.Exists(ready) && File.Exists(agent))
            return agent;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = sourceRoot,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "build", Path.Combine(sourceRoot, "src", "SDK", "AvaloniaDevTool.Agent", "AvaloniaDevTool.Agent.csproj"),
                     "-c", "Release", "--artifacts-path", output, "--disable-build-servers", "-v:minimal", "-p:AvaloniaVersion=" + avalonia })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(token);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
        var log = await stdout + await stderr;
        await File.WriteAllTextAsync(Path.Combine(output, "build.log"), log, token);
        if (process.ExitCode != 0 || !File.Exists(agent))
            throw new InvalidOperationException("agent_build_failed: " + log[^Math.Min(log.Length, 4000)..]);
        await File.WriteAllTextAsync(ready, "complete", token);
        return agent;
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
