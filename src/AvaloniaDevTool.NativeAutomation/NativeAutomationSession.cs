using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AvaloniaDevTool.NativeAutomation;

public sealed class NativeAutomationSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Process _worker;
    private readonly NativeAutomationOptions _options;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private long _requestId;
    private int _disposed;

    private NativeAutomationSession(Process worker, NativeTargetIdentity target, NativeAutomationOptions options)
    {
        _worker = worker;
        Target = target;
        _options = options;
        SessionId = Guid.NewGuid().ToString("N");
        // Drain diagnostics so a faulty provider cannot fill the stderr pipe.
        _ = DrainDiagnosticsAsync(_worker.StandardError);
    }

    public NativeTargetIdentity Target { get; }
    public string SessionId { get; }
    public bool IsAvailable
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            try { return !_worker.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }
    public bool IsAlive => IsAvailable;

    public static Task<NativeAutomationSession> AttachAsync(int processId, DateTimeOffset expectedStartTimeUtc,
        NativeAutomationOptions? options = null, CancellationToken cancellationToken = default) =>
        AttachAsync(new NativeTargetIdentity(processId, expectedStartTimeUtc.ToUniversalTime()), options, cancellationToken);

    public static async Task<NativeAutomationSession> AttachAsync(NativeTargetIdentity target,
        NativeAutomationOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new NativeAutomationException("unsupported_platform", "Native UI Automation is available only on Windows.");
        ArgumentNullException.ThrowIfNull(target);
        options ??= new NativeAutomationOptions();
        if (options.OperationTimeout <= TimeSpan.Zero || options.OperationTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(options), "OperationTimeout must be between zero and two minutes.");
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTarget(target);
        var workerPath = Path.GetFullPath(options.WorkerPath ?? Path.Combine(AppContext.BaseDirectory,
            "native-worker", "AvaloniaDevTool.NativeAutomation.Worker.dll"));
        if (!File.Exists(workerPath))
            throw new NativeAutomationException("worker_not_found", $"Native UI Automation worker was not found at '{workerPath}'.");
        var startInfo = new ProcessStartInfo
        {
            FileName = workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" : workerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(workerPath)!
        };
        if (workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(workerPath);
        Process worker;
        try { worker = Process.Start(startInfo) ?? throw new InvalidOperationException("Process.Start returned null."); }
        catch (Exception exception)
        { throw new NativeAutomationException("worker_start_failed", "Failed to start the native UI Automation worker.", exception); }
        var session = new NativeAutomationSession(worker, target, options);
        try
        {
            using var parent = Process.GetCurrentProcess();
            await session.SendAsync<bool>("attach", new NativeWorkerAttach(target, session.SessionId,
                parent.Id, parent.StartTime.ToUniversalTime()), cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<NativeWindowInfo[]> GetWindowsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<NativeWindowInfo[]>("windows", new { }, cancellationToken);

    public Task<NativeTreeResult> GetTreeAsync(NativeTreeQuery? query = null, CancellationToken cancellationToken = default) =>
        SendAsync<NativeTreeResult>("tree", query ?? new NativeTreeQuery(), cancellationToken);

    public Task<NativeFindResult> FindAsync(NativeFindQuery query, CancellationToken cancellationToken = default) =>
        SendAsync<NativeFindResult>("find", query, cancellationToken);

    public Task<NativeElementInfo> GetElementAsync(string nodeId, CancellationToken cancellationToken = default) =>
        SendAsync<NativeElementInfo>("element", nodeId, cancellationToken);

    public Task<NativeActionResult> ExecuteAsync(NativeActionRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<NativeActionResult>("action", request, cancellationToken);

    public Task<NativeActionResult> GetActionStatusAsync(string operationId, CancellationToken cancellationToken = default) =>
        SendAsync<NativeActionResult>("action-status", operationId, cancellationToken);

    public Task<NativeInputResult> SendInputAsync(NativeInputRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<NativeInputResult>("input", request, cancellationToken);

    public Task<NativeScreenshotResult> CaptureAsync(NativeScreenshotRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<NativeScreenshotResult>("screenshot", request, cancellationToken);

    private async Task<T> SendAsync<T>(string command, object payload, CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new NativeAutomationException("session_expired", "This native session has expired; attach again and obtain new node IDs.");
            ValidateTarget(Target);
            using var timeout = new CancellationTokenSource(_options.OperationTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                var request = new NativeWorkerRequest(Interlocked.Increment(ref _requestId), command,
                    JsonSerializer.SerializeToElement(payload, payload.GetType(), JsonOptions));
                await _worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions).AsMemory(), linked.Token).ConfigureAwait(false);
                await _worker.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
                var line = await _worker.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false);
                if (line is null)
                    throw new NativeAutomationException("worker_exited", "The native worker exited before responding.");
                var response = JsonSerializer.Deserialize<NativeWorkerResponse>(line, JsonOptions)
                    ?? throw new NativeAutomationException("worker_protocol_error", "The native worker returned an empty response.");
                if (response.Id != request.Id)
                    throw new NativeAutomationException("worker_protocol_error", "The native worker response ID did not match the request.");
                if (!response.Success)
                    throw new NativeAutomationException(response.ErrorCode ?? "native_error", response.ErrorMessage ?? "Native operation failed.");
                return response.Result!.Value.Deserialize<T>(JsonOptions)
                    ?? throw new NativeAutomationException("worker_protocol_error", "The native worker returned an empty result.");
            }
            catch (OperationCanceledException)
            {
                Invalidate();
                if (cancellationToken.IsCancellationRequested) throw;
                throw new NativeAutomationException("operation_timeout", "Native operation timed out. The worker was stopped; attach again and obtain new node IDs.");
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
            {
                Invalidate();
                throw new NativeAutomationException("worker_unavailable", "The native worker connection failed. Attach again and obtain new node IDs.", exception);
            }
            catch (NativeAutomationException exception) when (exception.Code is "worker_exited" or "worker_protocol_error")
            { Invalidate(); throw; }
        }
        finally { _requests.Release(); }
    }

    private static void ValidateTarget(NativeTargetIdentity target)
    {
        if (target.ProcessId <= 0)
            throw new NativeAutomationException("invalid_target", "A positive attached process ID is required.");
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != target.StartTimeUtc.UtcTicks)
                throw new NativeAutomationException("target_changed", "The attached process exited or its process ID was reused.");
        }
        catch (ArgumentException exception)
        { throw new NativeAutomationException("target_exited", "The attached process is no longer running.", exception); }
        catch (System.ComponentModel.Win32Exception exception)
        { throw new NativeAutomationException("target_access_denied", "Cannot verify the attached process identity.", exception); }
    }

    private void Invalidate()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (!_worker.HasExited) _worker.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        _ = ReleaseWorkerAsync(_worker);
    }

    private static async Task DrainDiagnosticsAsync(StreamReader reader)
    {
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0) { } }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task ReleaseWorkerAsync(Process worker)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await worker.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        finally { worker.Dispose(); }
    }

    public ValueTask DisposeAsync()
    {
        Invalidate();
        // The worker is disposable; never wait for a blocked native provider on the caller's path.
        return ValueTask.CompletedTask;
    }
}
