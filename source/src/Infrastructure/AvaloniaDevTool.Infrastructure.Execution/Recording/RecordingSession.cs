using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;

namespace AvaloniaDevTool.Recording;

public sealed record RecordingClientOptions
{
    public string? WorkerPath { get; init; }
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>Owns one independent recording worker; never calls the target application's UI thread.</summary>
public sealed class RecordingSession : IAsyncDisposable
{
    private static readonly byte[] NewLine = [(byte)'\n'];
    private static readonly JsonElement EmptyObject = CreateEmptyObject();
    private readonly Process _worker;
    private readonly RecordingClientOptions _options;
    private readonly RecordingAdmission _admission;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _monitorStop = new();
    private readonly TaskCompletionSource _releaseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<RecordingInputMarker> _markers = Channel.CreateBounded<RecordingInputMarker>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly string _recordingId;
    private readonly string _outputPath;
    private readonly DateTimeOffset _startedAt;
    private readonly int _targetProcessId;
    private readonly Task _diagnostics;
    private readonly StringBuilder _diagnosticTail = new();
    private readonly object _state = new();
    private readonly HashSet<string> _limitations = new(StringComparer.Ordinal);
    private string[] _localLimitations = [];
    private RecordingStatus _current;
    private Task? _monitor;
    private long _requestId;
    private long _markersDropped;
    private long _finalizingAt;
    private long _lastPersistedAt;
    private string? _lastPersistedState;
    private int _disposed;
    private int _released;
    private int _stopRequested;

    private RecordingSession(Process worker, RecordingAdmission admission, RecordingClientOptions options,
        RecordingWorkerStart request)
    {
        _worker = worker;
        _admission = admission;
        _options = options;
        _recordingId = request.RecordingId;
        _outputPath = request.OutputPath;
        _targetProcessId = request.ProcessId;
        _startedAt = DateTimeOffset.UtcNow;
        _current = new RecordingStatus
        {
            RecordingId = _recordingId, OutputPath = _outputPath, ProcessId = _targetProcessId, WorkerProcessId = worker.Id,
            State = "starting"
        };
        _diagnostics = DrainDiagnosticsAsync();
    }

    public RecordingStatus Current => Volatile.Read(ref _current);
    public bool IsActive => !IsTerminal(Current.State);
    public Task Completion => _releaseCompletion.Task;

    public void AddLimitation(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;
        lock (_state)
        {
            if (!_limitations.Add(message))
                return;
            _localLimitations = _limitations.ToArray();
            var updated = Current with { Limitations = MergeLimitations(Current.Limitations) };
            Volatile.Write(ref _current, updated);
            SaveStatus(updated, force: true);
        }
    }

    public static async Task<RecordingSession> StartAsync(RecordingStartRequest request,
        RecordingClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows())
            throw new RecordingException("unsupported_platform", "Window recording is available only on Windows.");
        options ??= new RecordingClientOptions();
        Validate(request, options);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTarget(request.ProcessId, request.StartTimeUtc);
        var root = Environment.GetEnvironmentVariable("AVDT_RUNTIME_ROOT") is { Length: > 0 } runtime
            ? runtime : AppContext.BaseDirectory;
        var workerDirectory = Path.Combine(root, "recording-worker");
        var workerExecutable = Path.Combine(workerDirectory, "AvaloniaDevTool.Recording.Worker.exe");
        var workerPath = Path.GetFullPath(options.WorkerPath ?? (File.Exists(workerExecutable)
            ? workerExecutable : Path.Combine(workerDirectory, "AvaloniaDevTool.Recording.Worker.dll")));
        if (!File.Exists(workerPath))
            throw new RecordingException("recording_worker_not_found", $"Recording worker was not found: {workerPath}");

        using var parent = Process.GetCurrentProcess();
        var id = "recording-" + Guid.NewGuid().ToString("N");
        var outputPath = CreateOutputPath(request.OutputDirectory, id);
        RecordingAdmission? admission = null;
        RecordingSession? session = null;
        try
        {
            admission = RecordingAdmission.Acquire();
            var start = new ProcessStartInfo
            {
                FileName = workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet" : workerPath,
                WorkingDirectory = Path.GetDirectoryName(workerPath)!, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (workerPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(workerPath);
            var worker = Process.Start(start) ?? throw new RecordingException("recording_worker_start_failed",
                "The recording worker did not start.");
            var payload = new RecordingWorkerStart
            {
                RecordingId = id, ProcessId = request.ProcessId, StartTimeUtc = request.StartTimeUtc,
                WindowHandle = request.WindowHandle, OutputPath = outputPath, Crop = request.Crop,
                FrameRate = request.FrameRate, Bitrate = request.Bitrate,
                MaxDurationSeconds = request.MaxDurationSeconds, MaxBytes = request.MaxBytes,
                ShowCursor = request.ShowCursor, MouseClicks = request.MouseClicks,
                ParentProcessId = parent.Id, ParentStartTimeUtc = parent.StartTime.ToUniversalTime()
            };
            session = new RecordingSession(worker, admission, options, payload);
            admission = null;
            var response = await session.SendAsync("start", payload, RecordingJsonContext.Default.StartCommand,
                options.StartupTimeout, cancellationToken).ConfigureAwait(false);
            session.Update(response);
            if (IsTerminal(session.Current.State))
            {
                await session.ReleaseAsync().ConfigureAwait(false);
                return session;
            }
            session._monitor = session.MonitorAsync();
            return session;
        }
        catch
        {
            if (session is not null)
                await session.DisposeAsync().ConfigureAwait(false);
            else
                admission?.Dispose();
            throw;
        }
    }

    public async Task<RecordingStatus> ReadStatusAsync(CancellationToken cancellationToken = default)
    {
        if (!IsActive)
            return Current;
        var status = await SendAsync("status", EmptyObject, RecordingJsonContext.Default.ControlCommand,
                _options.OperationTimeout, cancellationToken)
            .ConfigureAwait(false);
        Update(status);
        if (!IsActive)
            await ReleaseAsync().ConfigureAwait(false);
        return Current;
    }

    public async Task<RecordingStatus> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsActive)
            return Current;
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
            return Current;
        try
        {
            var status = await SendAsync("stop", EmptyObject, RecordingJsonContext.Default.ControlCommand,
                    _options.OperationTimeout, cancellationToken)
                .ConfigureAwait(false);
            Update(status);
            if (!IsActive)
                await ReleaseAsync().ConfigureAwait(false);
            return Current;
        }
        catch (OperationCanceledException)
        {
            // Cancellation while waiting for the IPC gate has not sent a stop to the worker.
            if (IsActive)
                Interlocked.Exchange(ref _stopRequested, 0);
            throw;
        }
    }

    /// <summary>Queues a video-only marker. It never delays or fails the target UI operation.</summary>
    public Task MarkInputAsync(string kind, double x, double y, string? button = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || !IsActive || Volatile.Read(ref _stopRequested) != 0 ||
            !double.IsFinite(x) || !double.IsFinite(y))
            return Task.CompletedTask;
        if (!_markers.Writer.TryWrite(new RecordingInputMarker(kind, x, y, button)))
            Interlocked.Increment(ref _markersDropped);
        WakeMonitor();
        return Task.CompletedTask;
    }

    private void WakeMonitor()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); }
            catch (SemaphoreFullException) { }
        }
    }

    private async Task MonitorAsync()
    {
        var statusClock = Stopwatch.StartNew();
        var statusInterval = TimeSpan.FromMilliseconds(500);
        try
        {
            while (!_monitorStop.IsCancellationRequested && IsActive)
            {
                var remaining = statusInterval - statusClock.Elapsed;
                await _wake.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
                    _monitorStop.Token).ConfigureAwait(false);
                // Bound each batch so continuous pointer input cannot starve status/finalization checks.
                for (var count = 0; count < 16 && IsActive && Volatile.Read(ref _stopRequested) == 0 &&
                     _markers.Reader.TryRead(out var marker); count++)
                {
                    RecordingStatus status;
                    try
                    {
                        status = await SendAsync("marker", marker, RecordingJsonContext.Default.MarkerCommand,
                            _options.OperationTimeout, _monitorStop.Token).ConfigureAwait(false);
                    }
                    catch (RecordingException error) when (error.Code is not ("recording_worker_exited" or
                        "recording_protocol_error" or "recording_worker_unavailable"))
                    {
                        Interlocked.Increment(ref _markersDropped);
                        AddLimitation("A video input marker could not be drawn: " + error.Code);
                        continue;
                    }
                    if (IsTerminal(status.State))
                    {
                        Update(status);
                        await ReleaseAsync().ConfigureAwait(false);
                        return;
                    }
                }
                if (IsActive && statusClock.Elapsed >= statusInterval)
                {
                    await ReadStatusAsync(_monitorStop.Token).ConfigureAwait(false);
                    statusClock.Restart();
                }
                var finalizing = Interlocked.Read(ref _finalizingAt);
                if (IsActive && (Current.State == "starting" && DateTimeOffset.UtcNow - _startedAt > _options.StartupTimeout ||
                    Current.State == "finalizing" && finalizing != 0 && Stopwatch.GetElapsedTime(finalizing) > _options.ShutdownTimeout))
                {
                    Fail("recording_stage_timeout", "The recorder did not finish starting or finalizing within its bounded timeout.");
                    await ReleaseAsync().ConfigureAwait(false);
                    return;
                }
                if (IsActive && Volatile.Read(ref _stopRequested) == 0 && _markers.Reader.TryPeek(out _))
                    WakeMonitor();
            }
        }
        catch (OperationCanceledException) when (_monitorStop.IsCancellationRequested) { }
        catch (Exception error) when (error is RecordingException or IOException or InvalidOperationException)
        {
            Fail("recording_worker_unavailable", error.Message);
            await ReleaseAsync().ConfigureAwait(false);
        }
    }

    private async Task<RecordingStatus> SendAsync<T>(string command, T payload,
        JsonTypeInfo<RecordingPipeCommand<T>> typeInfo, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _released) != 0)
                return Current;
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stopping.CancelAfter(timeout);
            var request = new RecordingPipeCommand<T>(Interlocked.Increment(ref _requestId), command, payload);
            // Source-generated serialization writes UTF-8 directly; no marker JsonElement or UTF-16 JSON copy.
            var input = _worker.StandardInput.BaseStream;
            await JsonSerializer.SerializeAsync(input, request, typeInfo, stopping.Token).ConfigureAwait(false);
            await input.WriteAsync(NewLine, stopping.Token).ConfigureAwait(false);
            await input.FlushAsync(stopping.Token).ConfigureAwait(false);
            var line = await _worker.StandardOutput.ReadLineAsync(stopping.Token).ConfigureAwait(false);
            if (line is null)
                throw new RecordingException("recording_worker_exited", "The recording worker exited. " + Diagnostics());
            if (line.Length > 128 * 1024)
                throw new RecordingException("recording_protocol_error", "The recording worker response was too large.");
            var response = JsonSerializer.Deserialize(line, RecordingJsonContext.Default.RecordingPipeResponse)
                           ?? throw new RecordingException("recording_protocol_error", "The worker response was empty.");
            if (response.Id != request.Id)
                throw new RecordingException("recording_protocol_error", "The recording worker response ID did not match.");
            if (!response.Success)
                throw new RecordingException(response.ErrorCode ?? "recording_failed",
                    response.ErrorMessage ?? "The recording worker operation failed.");
            return response.Result ?? throw new RecordingException("recording_protocol_error", "The recording worker returned no status.");
        }
        catch (OperationCanceledException)
        {
            Fail("recording_operation_cancelled", "A recording control request was cancelled or timed out; the worker is being stopped.");
            _ = ReleaseAsync();
            throw;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
        {
            Fail("recording_worker_unavailable", error.Message);
            _ = ReleaseAsync();
            throw new RecordingException("recording_worker_unavailable", "The recording worker connection failed.", error);
        }
        finally { _requests.Release(); }
    }

    private void Update(RecordingStatus status)
    {
        if (status.RecordingId != _recordingId ||
            !string.Equals(status.OutputPath, _outputPath, StringComparison.OrdinalIgnoreCase))
            throw new RecordingException("recording_protocol_error", "The worker returned a different recording identity.");
        lock (_state)
        {
            var previous = Current;
            // A slower status response must not revive a recording another request already finalized.
            if (IsTerminal(previous.State))
                return;
            var terminal = IsTerminal(status.State);
            if (status.State == "finalizing" && previous.State != "finalizing")
                Interlocked.CompareExchange(ref _finalizingAt, Stopwatch.GetTimestamp(), 0);
            var bytes = File.Exists(_outputPath) ? new FileInfo(_outputPath).Length : status.BytesWritten;
            var result = status with
            {
                ProcessId = _targetProcessId, WorkerProcessId = _worker.Id,
                StartedAtUtc = status.StartedAtUtc ?? previous.StartedAtUtc,
                StoppedAtUtc = terminal ? status.StoppedAtUtc ?? previous.StoppedAtUtc ?? DateTimeOffset.UtcNow : null,
                BytesWritten = bytes, InputMarkersDropped = Interlocked.Read(ref _markersDropped),
                Limitations = MergeLimitations(status.Limitations)
            };
            if (terminal && status.State == "completed" && bytes > 0)
            {
                using var stream = File.OpenRead(_outputPath);
                result = result with
                {
                    ArtifactId = _recordingId,
                    Sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant()
                };
            }
            if (terminal && status.State == "completed" && bytes == 0)
                result = result with { State = "failed", ErrorCode = "empty_recording", ErrorMessage = "The recording contains no video bytes." };
            Volatile.Write(ref _current, result);
            SaveStatus(result);
        }
    }

    private void Fail(string code, string message)
    {
        lock (_state)
        {
            if (IsTerminal(Current.State))
                return;
            var failed = Current with
            {
                State = "failed", ErrorCode = code, ErrorMessage = message,
                StoppedAtUtc = DateTimeOffset.UtcNow, EndReason = "control_failure"
            };
            Volatile.Write(ref _current, failed);
            SaveStatus(failed);
        }
    }

    private string[] MergeLimitations(string[] workerLimitations)
    {
        if (_localLimitations.Length == 0)
            return workerLimitations;
        if (workerLimitations.Length == 0)
            return _localLimitations;
        return workerLimitations.Concat(_localLimitations).Distinct(StringComparer.Ordinal).ToArray();
    }

    private void SaveStatus(RecordingStatus status, bool force = false)
    {
        if (!force && !IsTerminal(status.State) && status.State == _lastPersistedState &&
            Stopwatch.GetElapsedTime(_lastPersistedAt) < TimeSpan.FromSeconds(1))
            return;
        try
        {
            RecordingArtifacts.Save(status);
            _lastPersistedAt = Stopwatch.GetTimestamp();
            _lastPersistedState = status.State;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Volatile.Write(ref _current, status with
            {
                Limitations = [.. status.Limitations, "artifact_index_write_failed: " + error.Message]
            });
        }
    }

    private async Task DrainDiagnosticsAsync()
    {
        var buffer = new char[1024];
        try
        {
            int count;
            while ((count = await _worker.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                lock (_diagnosticTail)
                {
                    _diagnosticTail.Append(buffer, 0, count);
                    if (_diagnosticTail.Length > 4096)
                        _diagnosticTail.Remove(0, _diagnosticTail.Length - 4096);
                }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private string Diagnostics() { lock (_diagnosticTail) return _diagnosticTail.ToString(); }

    private async Task ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            await _releaseCompletion.Task.ConfigureAwait(false);
            return;
        }
        _monitorStop.Cancel();
        _markers.Writer.TryComplete();
        try
        {
            try { _worker.StandardInput.Close(); }
            catch (IOException) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _worker.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                if (!_worker.HasExited)
                    _worker.Kill(entireProcessTree: true);
                using var killedTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _worker.WaitForExitAsync(killedTimeout.Token).ConfigureAwait(false);
            }
            await _diagnostics.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or
                                      OperationCanceledException or TimeoutException) { }
        finally
        {
            _worker.Dispose();
            _admission.Dispose();
            _releaseCompletion.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            if (IsActive)
            {
                using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
                await StopAsync(timeout.Token).ConfigureAwait(false);
                while (IsActive)
                {
                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                    await ReadStatusAsync(timeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or RecordingException or IOException)
        {
            Fail("recording_shutdown_incomplete", "The recording did not finalize before shutdown: " + error.Message);
        }
        finally { await ReleaseAsync().ConfigureAwait(false); }
        if (_monitor is not null)
            await _monitor.ConfigureAwait(false);
    }

    public static void Validate(RecordingStartRequest request, RecordingClientOptions? options = null)
    {
        if (request.ProcessId <= 0 || request.WindowHandle == 0 || request.StartTimeUtc == default)
            throw new RecordingException("invalid_target", "A verified process, start time and window handle are required.");
        if (request.FrameRate is < 1 or > 60 || request.Bitrate is < 100_000 or > 20_000_000 ||
            request.MaxDurationSeconds is < 1 or > 3600 || request.MaxBytes is < 2 * 1024 * 1024 or > 2L * 1024 * 1024 * 1024)
            throw new RecordingException("invalid_recording_options",
                "Recording requires fps 1..60, bitrate 100000..20000000, duration 1..3600 seconds and budget 2 MiB..2 GiB.");
        if (request.Crop is { } crop && (crop.X < 0 || crop.Y < 0 || crop.Width <= 0 || crop.Height <= 0 ||
            (long)crop.Width * crop.Height > 33_177_600))
            throw new RecordingException("invalid_recording_region", "The crop must have nonnegative origin and positive bounded dimensions.");
        if (request.OutputDirectory is { } directory && !Path.IsPathFullyQualified(directory))
            throw new RecordingException("invalid_output_directory", "The output directory must be an absolute path on the execution computer.");
        if (options is not null && new[] { options.OperationTimeout, options.StartupTimeout, options.ShutdownTimeout }
            .Any(timeout => timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2)))
            throw new ArgumentOutOfRangeException(nameof(options), "Recording timeouts must be positive and at most two minutes.");
    }

    private static string CreateOutputPath(string? directory, string id)
    {
        var destination = Path.GetFullPath(directory ?? RecordingArtifacts.DefaultDirectory);
        Directory.CreateDirectory(destination);
        var path = Path.Combine(destination, DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + id[10..] + ".mp4");
        var probe = Path.Combine(destination, ".recording-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        try { using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { } }
        finally { if (File.Exists(probe)) File.Delete(probe); }
        return path;
    }

    private static void ValidateTarget(int processId, DateTimeOffset expectedStart)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited || process.StartTime.ToUniversalTime() != expectedStart.UtcDateTime)
                throw new RecordingException("recording_target_changed", "The selected process exited or its ID was reused.");
        }
        catch (ArgumentException error)
        {
            throw new RecordingException("recording_target_exited", "The selected recording process is no longer running.", error);
        }
    }

    private static JsonElement CreateEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }
    private static bool IsTerminal(string state) => state is "completed" or "completed_with_gaps" or "failed" or "interrupted";
}
