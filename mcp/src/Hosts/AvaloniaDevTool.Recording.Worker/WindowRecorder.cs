using System.Diagnostics;
using System.Runtime.InteropServices;
using ScreenRecorderLib;

namespace AvaloniaDevTool.Recording.Worker;

internal sealed class WindowRecorder : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly object _overlaySync = new();
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Timer _monitor;
    private Task _initializationTask = Task.CompletedTask;
    private Task _stopTask = Task.CompletedTask;
    private RecordingWorkerStart? _options;
    private RecordingStatus? _status;
    private Recorder? _recorder;
    private FileStream? _output;
    private ClickOverlay? _clickOverlay;
    private long _initializationStarted;
    private long _recordingStarted;
    private long _recordingStopped;
    private long _lastMemorySample;
    private int _sourceLeft, _sourceTop, _sourceWidth, _sourceHeight;
    private int _outputWidth, _outputHeight;
    private int _initialFrameWidth, _initialFrameHeight;
    private uint _initialDpi;
    private bool _stopRequested, _disposed;

    internal bool IsStarted => _options is not null;

    internal WindowRecorder()
    {
        SetProcessDpiAwarenessContext(new nint(-4));
        _monitor = new Timer(_ => Monitor(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal RecordingStatus Start(RecordingWorkerStart options)
    {
        lock (_sync)
        {
            if (_disposed)
                throw new RecordingException("recording_worker_stopping", "The recording worker is shutting down.");
            if (_options is not null)
                throw new RecordingException("recording_busy", "This worker already owns a recording.");
            Validate(options);
            var hwnd = new nint(options.WindowHandle);
            var frame = FrameBounds(hwnd);
            _initialFrameWidth = frame.Right - frame.Left;
            _initialFrameHeight = frame.Bottom - frame.Top;
            _initialDpi = GetDpiForWindow(hwnd);
            var crop = options.Crop;
            _sourceLeft = 0;
            _sourceTop = 0;
            _sourceWidth = frame.Right - frame.Left;
            _sourceHeight = frame.Bottom - frame.Top;
            if (crop is not null)
            {
                var origin = new Point();
                if (!ClientToScreen(hwnd, ref origin))
                    throw new RecordingException("geometry_unavailable", "Cannot locate the window client area.");
                _sourceLeft = origin.X - frame.Left + crop.X;
                _sourceTop = origin.Y - frame.Top + crop.Y;
                _sourceWidth = crop.Width;
                _sourceHeight = crop.Height;
            }
            if (_sourceLeft < 0 || _sourceTop < 0 || _sourceWidth <= 0 || _sourceHeight <= 0 ||
                (long)_sourceLeft + _sourceWidth > frame.Right - frame.Left ||
                (long)_sourceTop + _sourceHeight > frame.Bottom - frame.Top)
                throw new RecordingException("invalid_region", "The requested crop is outside the selected window surface.");
            if ((long)(frame.Right - frame.Left) * (frame.Bottom - frame.Top) > 16_777_216)
                throw new RecordingException("source_too_large", "Window recording supports at most 16 megapixels of source surface.");
            var scale = Math.Min(1, 1920.0 / Math.Max(_sourceWidth, _sourceHeight));
            _outputWidth = Math.Max(2, (int)(_sourceWidth * scale) & ~1);
            _outputHeight = Math.Max(2, (int)(_sourceHeight * scale) & ~1);
            _options = options;
            _status = new RecordingStatus
            {
                RecordingId = options.RecordingId,
                OutputPath = options.OutputPath,
                ProcessId = options.ProcessId,
                Width = _outputWidth,
                Height = _outputHeight,
                HardwareEncodingRequested = true,
                HardwareEncodingVerified = false,
                Encoder = "ScreenRecorderLib 7.0.1 / MediaFoundation H264",
                Limitations = ["Hardware encoder verification is pending while the recorder initializes.",
                    "Window capture excludes independently owned popup windows."]
            };
            _initializationStarted = Stopwatch.GetTimestamp();
            _monitor.Change(100, 100);
            _initializationTask = Task.Run(Initialize);
        }
        return Snapshot();
    }

    private void Initialize()
    {
        try
        {
            var options = _options!;
            var source = new WindowRecordingSource(new nint(options.WindowHandle))
            {
                IsCursorCaptureEnabled = options.ShowCursor,
                IsBorderRequired = true,
                SourceRect = options.Crop is null ? null : new ScreenRect(_sourceLeft, _sourceTop, _sourceWidth, _sourceHeight),
                OutputSize = new ScreenSize(_outputWidth, _outputHeight),
                Stretch = StretchMode.Uniform,
                AnchorPoint = Anchor.Center
            };
            _clickOverlay = options.MouseClicks ? new ClickOverlay() : null;
            var recorderOptions = new RecorderOptions
            {
                SourceOptions = new SourceOptions { RecordingSources = [source] },
                OutputOptions = new OutputOptions
                {
                    RecorderMode = RecorderMode.Video,
                    OutputFrameSize = new ScreenSize(_outputWidth, _outputHeight),
                    Stretch = StretchMode.Fill,
                    IsVideoFramePreviewEnabled = false
                },
                AudioOptions = new AudioOptions { IsAudioEnabled = false },
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Encoder = new H264VideoEncoder
                    {
                        BitrateMode = H264BitrateControlMode.UnconstrainedVBR,
                        EncoderProfile = H264Profile.Main
                    },
                    Bitrate = options.Bitrate,
                    Framerate = options.FrameRate,
                    IsFixedFramerate = true,
                    IsFragmentedMp4Enabled = true,
                    IsMp4FastStartEnabled = false,
                    IsHardwareEncodingEnabled = true,
                    IsLowLatencyEnabled = true,
                    IsThrottlingDisabled = false
                },
                MouseOptions = new MouseOptions
                {
                    IsMousePointerEnabled = options.ShowCursor,
                    IsMouseClicksDetected = options.MouseClicks,
                    MouseLeftClickDetectionColor = "#2D9EFF",
                    MouseRightClickDetectionColor = "#FF992D",
                    MouseClickDetectionRadius = 25,
                    MouseClickDetectionDuration = 400,
                    MouseClickDetectionMode = MouseDetectionMode.Hook
                },
                OverlayOptions = new OverLayOptions { Overlays = _clickOverlay?.Images ?? [] },
                SnapshotOptions = new SnapshotOptions { SnapshotsWithVideo = false },
                LogOptions = new LogOptions { IsLogEnabled = false }
            };
            var recorder = Recorder.CreateRecorder(recorderOptions);
            recorder.OnStatusChanged += (_, eventArgs) =>
            {
                lock (_sync)
                {
                    if (_status is null || _completed.Task.IsCompleted) return;
                    if (eventArgs.Status == RecorderStatus.Recording && !_stopRequested)
                    {
                        if (_recordingStarted == 0)
                        {
                            _recordingStarted = Stopwatch.GetTimestamp();
                            _status = _status with { StartedAtUtc = DateTimeOffset.UtcNow };
                        }
                        var verified = recorder.EncoderHardwareState == 1;
                        _status = _status with
                        {
                            State = "recording",
                            HardwareEncodingVerified = verified,
                            Encoder = string.IsNullOrWhiteSpace(recorder.VideoEncoderName)
                                ? "MediaFoundation H264 (" + (verified ? "hardware verified" : "hardware unverified") + ")"
                                : recorder.VideoEncoderName,
                            Limitations = verified
                                ? ["Window capture excludes independently owned popup windows."]
                                : ["Actual hardware encoding could not be verified.", "Window capture excludes independently owned popup windows."]
                        };
                    }
                    else if (eventArgs.Status == RecorderStatus.Finishing)
                        _status = _status with { State = "finalizing" };
                }
            };
            recorder.OnRecordingComplete += (_, _) => Finish(null);
            recorder.OnRecordingFailed += (_, eventArgs) => Finish(eventArgs.Error);
            bool discard;
            lock (_sync)
            {
                discard = _disposed;
                if (!discard)
                {
                    _recorder = recorder;
                    _output = new FileStream(options.OutputPath, FileMode.CreateNew, FileAccess.ReadWrite,
                        FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                }
            }
            if (discard) { recorder.Dispose(); return; }
            recorder.Record(_output);
            bool stop;
            lock (_sync) stop = _stopRequested;
            if (stop) recorder.Stop();
        }
        catch (Exception error) { Finish(error.Message); }
    }

    internal RecordingStatus Snapshot()
    {
        lock (_sync)
        {
            if (_status is null)
                throw new RecordingException("recording_not_started", "Start a recording before reading its state.");
            if (_lastMemorySample == 0 || _completed.Task.IsCompleted ||
                Stopwatch.GetElapsedTime(_lastMemorySample) >= TimeSpan.FromSeconds(1))
            {
                var info = GC.GetGCMemoryInfo();
                _status = _status with
                {
                    ManagedMemoryObserved = true,
                    ManagedHeapBytes = GC.GetTotalMemory(false),
                    ManagedAllocatedBytes = GC.GetTotalAllocatedBytes(false),
                    Gen0Collections = GC.CollectionCount(0),
                    Gen1Collections = GC.CollectionCount(1),
                    Gen2Collections = GC.CollectionCount(2),
                    LastGcIndex = info.Index == 0 ? null : info.Index,
                    LohBytesAfterLastCollection = info.Index != 0 && info.GenerationInfo.Length > 3
                        ? info.GenerationInfo[3].SizeAfterBytes : null,
                    TexturePoolObserved = _recorder?.TexturePoolObserved ?? false,
                    EncoderTexturePoolCapacity = _recorder?.EncoderTexturePoolCapacity ?? 0,
                    EncoderTexturePoolAllocated = _recorder?.EncoderTexturePoolAllocated ?? 0,
                    EncoderTexturePoolInUse = _recorder?.EncoderTexturePoolInUse ?? 0,
                    EncoderTexturePoolPeakInUse = _recorder?.EncoderTexturePoolPeakInUse ?? 0,
                    EncoderTexturePoolReuses = _recorder?.EncoderTexturePoolReuses ?? 0,
                    EncoderTexturePoolTimeouts = _recorder?.EncoderTexturePoolTimeouts ?? 0,
                    EncoderTexturePoolBytes = _recorder?.EncoderTexturePoolBytes ?? 0
                };
                _lastMemorySample = Stopwatch.GetTimestamp();
            }
            return _status with
            {
                ElapsedSeconds = _recordingStarted == 0 ? 0 : _status.StoppedAtUtc is null
                    ? Stopwatch.GetElapsedTime(_recordingStarted,
                        _recordingStopped == 0 ? Stopwatch.GetTimestamp() : _recordingStopped).TotalSeconds
                    : _status.ElapsedSeconds,
                BytesWritten = FileLength(_status.OutputPath)
            };
        }
    }

    internal RecordingStatus Stop(string reason)
    {
        lock (_sync)
        {
            if (_status is null)
                throw new RecordingException("recording_not_started", "No recording is active.");
            if (_disposed || _stopRequested || _completed.Task.IsCompleted) return Snapshot();
            _stopRequested = true;
            if (_recordingStarted != 0) _recordingStopped = Stopwatch.GetTimestamp();
            _status = _status with { State = "finalizing", EndReason = reason };
            if (_recorder is { } recorder)
            {
                _stopTask = Task.Run(() =>
                {
                    try { recorder.Stop(); }
                    catch (Exception error) { Finish(error.Message); }
                });
            }
        }
        return Snapshot();
    }

    internal RecordingStatus Mark(RecordingInputMarker marker)
    {
        Recorder? recorder;
        ClickOverlay? overlay;
        double outputX, outputY;
        bool inside;
        lock (_sync)
        {
            if (!double.IsFinite(marker.X) || !double.IsFinite(marker.Y))
                throw new RecordingException("invalid_marker", "Marker coordinates must be finite screen pixels.");
            if (_recorder is null || _status?.State != "recording" || _clickOverlay is null)
                return Snapshot();
            var handle = new nint(_options!.WindowHandle);
            if (!IsWindow(handle))
                return Stop("target_exited");
            Rect frame;
            try { frame = FrameBounds(handle); }
            catch (RecordingException)
            {
                // A late optional pointer event must not abort an otherwise valid MP4 finalization.
                return IsWindow(handle) ? Snapshot() : Stop("target_exited");
            }
            var x = marker.X - frame.Left - _sourceLeft;
            var y = marker.Y - frame.Top - _sourceTop;
            var sourceWidth = _options.Crop is null ? frame.Right - frame.Left : _sourceWidth;
            var sourceHeight = _options.Crop is null ? frame.Bottom - frame.Top : _sourceHeight;
            var scale = Math.Min(_outputWidth / (double)sourceWidth, _outputHeight / (double)sourceHeight);
            inside = x >= 0 && y >= 0 && x < sourceWidth && y < sourceHeight;
            outputX = x * scale + (_outputWidth - sourceWidth * scale) / 2;
            outputY = y * scale + (_outputHeight - sourceHeight * scale) / 2;
            recorder = _recorder;
            overlay = _clickOverlay;
        }
        lock (_overlaySync)
        {
            if (inside)
                overlay.Update(recorder, outputX, outputY, marker.Kind, marker.Button, Stopwatch.GetTimestamp());
            else
                overlay.UpdateOutside(recorder, marker.Kind);
        }
        return Snapshot();
    }

    private void Monitor()
    {
        try
        {
            var status = Snapshot();
            if (_completed.Task.IsCompleted || _stopRequested) return;
            var options = _options!;
            if (status.State == "starting" && Stopwatch.GetElapsedTime(_initializationStarted).TotalSeconds >= 30)
            {
                lock (_sync)
                    if (_status?.State == "starting")
                        _status = _status with
                        {
                            ErrorCode = "recording_start_timeout",
                            ErrorMessage = "The recording backend did not become ready within thirty seconds."
                        };
                Stop("startup_timeout");
                return;
            }
            if (status.ElapsedSeconds >= options.MaxDurationSeconds) Stop("duration_limit");
            else if (status.BytesWritten >= options.MaxBytes - 1024 * 1024) Stop("size_limit");
            else if (!IsTargetAlive(options.ProcessId, options.StartTimeUtc) || !WindowStillBelongs(options)) Stop("target_exited");
            else if (IsIconic(new nint(options.WindowHandle))) Stop("window_minimized");
            else if (options.Crop is not null && CropGeometryChanged(options)) Stop("window_geometry_changed");
            else if (options.Crop is null && SourceTooLarge(options.WindowHandle)) Stop("source_too_large");
            else if (options.ParentProcessId > 0 && options.ParentStartTimeUtc is { } parentStart &&
                     !IsTargetAlive(options.ParentProcessId, parentStart)) Stop("parent_exited");
            Recorder? recorder;
            ClickOverlay? overlay;
            lock (_sync) { recorder = _stopRequested ? null : _recorder; overlay = _clickOverlay; }
            if (recorder is not null && overlay is not null)
                lock (_overlaySync)
                    overlay.HideExpired(recorder, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine("recording_monitor: " + error.Message);
            try { Stop("monitor_failed"); } catch (RecordingException) { }
        }
    }

    private void Finish(string? error)
    {
        lock (_sync)
        {
            if (_status is null || _completed.Task.IsCompleted) return;
            error ??= _status.ErrorMessage;
            try { _output?.Flush(); _output?.Dispose(); }
            catch (IOException exception) { error ??= exception.Message; }
            _output = null;
            _status = _status with
            {
                State = error is null ? "completed" : "failed",
                StoppedAtUtc = DateTimeOffset.UtcNow,
                ElapsedSeconds = _recordingStarted == 0 ? 0 : Stopwatch.GetElapsedTime(_recordingStarted,
                    _recordingStopped == 0 ? Stopwatch.GetTimestamp() : _recordingStopped).TotalSeconds,
                ErrorCode = error is null ? null : _status.ErrorCode ?? (_recorder?.EncoderTexturePoolTimeouts > 0
                    ? "encoder_backpressure_timeout" : _recorder?.EncoderHardwareState == 0
                        ? "hardware_path_unavailable" : "encoding_failed"),
                ErrorMessage = error,
                EndReason = _status.EndReason ?? (error is null ? "completed" : "encoding_failed")
            };
            if (!_disposed) _monitor.Change(Timeout.Infinite, Timeout.Infinite);
            _completed.TrySetResult();
        }
    }

    internal async Task WaitForCompletionAsync(TimeSpan timeout)
    {
        if (_options is null) return;
        try { await _completed.Task.WaitAsync(timeout); }
        catch (TimeoutException) { }
    }

    private static void Validate(RecordingWorkerStart options)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            throw new RecordingException("unsupported_platform", "Window recording requires Windows 10 build 19041 or newer.");
        if (options.FrameRate is < 1 or > 60 || options.Bitrate is < 100_000 or > 20_000_000 ||
            options.MaxDurationSeconds is < 1 or > 3600 || options.MaxBytes is < 2 * 1024 * 1024 or > 2L * 1024 * 1024 * 1024)
            throw new RecordingException("invalid_recording_options", "Recording options are outside supported limits.");
        if (!IsTargetAlive(options.ProcessId, options.StartTimeUtc))
            throw new RecordingException("target_changed", "The target process exited or its process ID was reused.");
        var hwnd = new nint(options.WindowHandle);
        GetWindowThreadProcessId(hwnd, out var owner);
        if (!IsWindow(hwnd) || owner != options.ProcessId || !IsWindowVisible(hwnd) || IsIconic(hwnd))
            throw new RecordingException("invalid_window", "Select a visible, non-minimized window belonging to the attached process.");
        if (!Path.IsPathFullyQualified(options.OutputPath) || !options.OutputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            throw new RecordingException("invalid_output_path", "The recording output must be an absolute MP4 path.");
        if (File.Exists(options.OutputPath))
            throw new RecordingException("output_exists", "The recording output already exists.");
        var directory = Path.GetDirectoryName(options.OutputPath)!;
        if (!Directory.Exists(directory))
            throw new RecordingException("directory_not_found", "The recording output directory does not exist.");
        if (new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace < 16 * 1024 * 1024)
            throw new RecordingException("insufficient_space", "Recording requires at least 16 MiB of free disk space.");
    }

    private static bool IsTargetAlive(int pid, DateTimeOffset start)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime() == start.UtcDateTime;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    private static bool WindowStillBelongs(RecordingWorkerStart options)
    {
        var window = new nint(options.WindowHandle);
        return IsWindow(window) && GetWindowThreadProcessId(window, out var owner) != 0 && owner == options.ProcessId;
    }

    private static Rect FrameBounds(nint window)
    {
        if (DwmGetWindowAttribute(window, 9, out var rect, Marshal.SizeOf<Rect>()) != 0 && !GetWindowRect(window, out rect))
            throw new RecordingException("geometry_unavailable", "Cannot read the window capture bounds.");
        return rect;
    }

    private static bool SourceTooLarge(long windowHandle)
    {
        var bounds = FrameBounds(new nint(windowHandle));
        return (long)(bounds.Right - bounds.Left) * (bounds.Bottom - bounds.Top) > 16_777_216;
    }

    private bool CropGeometryChanged(RecordingWorkerStart options)
    {
        var window = new nint(options.WindowHandle);
        var frame = FrameBounds(window);
        return GetDpiForWindow(window) != _initialDpi ||
            frame.Right - frame.Left != _initialFrameWidth || frame.Bottom - frame.Top != _initialFrameHeight;
    }

    private static long FileLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Timer.Dispose does not drain queued callbacks. They read the native encoder
        // and may update overlays, so complete them before deleting that encoder.
        await _monitor.DisposeAsync().ConfigureAwait(false);
        // Native completion can be raised before Initialize or a queued Stop returns.
        // Both tasks remain owners of the recorder until their calls have finished.
        await _initializationTask.ConfigureAwait(false);
        await _stopTask.ConfigureAwait(false);
        if (_completed.Task.IsCompleted) _recorder?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref Point point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out Rect rect, int size);
}
