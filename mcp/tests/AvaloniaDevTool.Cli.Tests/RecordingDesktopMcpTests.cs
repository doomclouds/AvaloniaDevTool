using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AvaloniaDevTool.Protocol;
using AvaloniaDevTool.Recording;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

[Collection("Recording desktop integration")]
public sealed class RecordingDesktopMcpTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static bool CanRunDesktopRecording => OperatingSystem.IsWindows() && Environment.UserInteractive &&
        Environment.GetEnvironmentVariable("AVDT_RUN_RECORDING_DESKTOP_TESTS") == "1";

    [Theory(SkipUnless = nameof(CanRunDesktopRecording),
        Skip = "Set AVDT_RUN_RECORDING_DESKTOP_TESTS=1 on an interactive Windows desktop to record the owned fixture.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stdio_records_control_region_with_Agent_clicks_streaming_growth_and_window_movement(bool controlsOnly)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(100));
        var token = deadline.Token;
        var evidenceRoot = Environment.GetEnvironmentVariable("AVDT_RECORDING_EVIDENCE_DIR") ??
                           Path.Combine(Metadata("PreviewRepositoryPath"), "artifacts", "recording-verification");
        var runDirectory = Path.GetFullPath(Path.Combine(evidenceRoot,
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-mcp-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(runDirectory);
        var videoDirectory = Path.Combine(runDirectory, "videos");
        var actions = new List<object>();
        var growth = new List<object>();
        RecordingStatus? latest = null;
        string? failure = null;

        await using var fixture = await OwnedSample.StartAsync(token);
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "Owned desktop recording verification",
            Arguments = [Metadata("PreviewCliPath"), "mcp", "--pipe", fixture.PipeName],
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        }), cancellationToken: token);
        try
        {
            var counter = await FindAsync(client, "CounterButton", token);
            var status = await FindAsync(client, "InteractionStatus", token);
            var root = await FindAsync(client, "AutomationMainWindow", token);
            var sourceBefore = await CallAsync(client, "ui-screenshot", token, ("nodeId", root));
            await File.WriteAllBytesAsync(Path.Combine(runDirectory, "source-before.png"),
                Assert.Single(sourceBefore.Content.OfType<ImageContentBlock>()).DecodedData.ToArray(), token);
            latest = Read<RecordingStatus>(await CallAsync(client, "recording-start", token,
                ("nodeIds", controlsOnly ? new[] { counter, status } : null),
                ("outputDirectory", controlsOnly ? videoDirectory : null),
                ("frameRate", 30), ("maxDurationSeconds", 20), ("mouseClicks", true)));
            latest = await WaitForStateAsync(client, latest.RecordingId, "recording", TimeSpan.FromSeconds(20), token);
            Assert.True(latest.Width > 0 && latest.Height > 0);
            Assert.Equal(Path.GetFullPath(controlsOnly ? videoDirectory : RecordingArtifacts.DefaultDirectory),
                Path.GetDirectoryName(latest.OutputPath), ignoreCase: true);
            var started = latest.StartedAtUtc;
            var videoClock = Stopwatch.StartNew();
            var initialLength = Length(latest.OutputPath);
            growth.Add(new { Seconds = videoClock.Elapsed.TotalSeconds, Bytes = initialLength });

            for (var index = 0; index < 5; index++)
            {
                var at = DateTimeOffset.UtcNow;
                Read<InputResult>(await CallAsync(client, "ui-click", token, ("nodeId", counter)));
                actions.Add(new
                {
                    Kind = "AgentClick", Index = index + 1, AtUtc = at,
                    SecondsAfterReady = videoClock.Elapsed.TotalSeconds,
                    SecondsAfterWorkerStart = started is { } start ? (at - start).TotalSeconds : (double?)null
                });
                await Task.Delay(400, token);
            }

            var beforeMove = fixture.MoveWindow(16, 12);
            actions.Add(new { Kind = "OwnedWindowMove", AtUtc = DateTimeOffset.UtcNow,
                SecondsAfterReady = videoClock.Elapsed.TotalSeconds, From = beforeMove, DeltaX = 16, DeltaY = 12 });
            await Task.Delay(500, token);
            Read<InputResult>(await CallAsync(client, "ui-click", token, ("nodeId", counter)));
            actions.Add(new { Kind = "AgentClickAfterMove", AtUtc = DateTimeOffset.UtcNow,
                SecondsAfterReady = videoClock.Elapsed.TotalSeconds });
            Read<InputResult>(await CallAsync(client, "ui-input", token,
                ("nodeId", counter), ("kind", "Wheel"), ("deltaY", -1d)));
            actions.Add(new { Kind = "AgentWheel", AtUtc = DateTimeOffset.UtcNow,
                SecondsAfterReady = videoClock.Elapsed.TotalSeconds });

            var growthDeadline = Stopwatch.StartNew();
            while (growthDeadline.Elapsed < TimeSpan.FromSeconds(6))
            {
                await Task.Delay(500, token);
                var bytes = Length(latest.OutputPath);
                growth.Add(new { Seconds = videoClock.Elapsed.TotalSeconds, Bytes = bytes });
                latest = Read<RecordingStatus>(await CallAsync(client, "recording-status", token,
                    ("recordingId", latest.RecordingId)));
                Assert.Equal("recording", latest.State);
                if (bytes > Math.Max(initialLength, 1024))
                    break;
            }
            Assert.True(Length(latest.OutputPath) > Math.Max(initialLength, 1024),
                "The MP4 must grow while recording, before recording-stop finalizes it.");
            Read<RecordingStatus>(await CallAsync(client, "recording-stop", token,
                ("recordingId", latest.RecordingId)));
            latest = await WaitForStateAsync(client, latest.RecordingId, "completed", TimeSpan.FromSeconds(20), token);
            Assert.Equal(0, latest.InputMarkersDropped);
            Assert.DoesNotContain(latest.Limitations,
                limitation => limitation.Contains("Agent framework input highlights are unavailable", StringComparison.Ordinal));
            Assert.NotNull(latest.ArtifactId);
            Assert.True(latest.BytesWritten > 1024);
            Assert.Equal(new FileInfo(latest.OutputPath).Length, latest.BytesWritten);
            var bytesWritten = await File.ReadAllBytesAsync(latest.OutputPath, token);
            Assert.True(bytesWritten.AsSpan(0, Math.Min(bytesWritten.Length, 256)).IndexOf("ftyp"u8) >= 0,
                "The saved recording must have an MP4 file-type box.");
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytesWritten)).ToLowerInvariant(), latest.Sha256);
            var repeated = Read<RecordingStatus>(await CallAsync(client, "recording-stop", token,
                ("recordingId", latest.RecordingId)));
            Assert.Equal(latest.Sha256, repeated.Sha256);

            var downloaded = Path.Combine(runDirectory, "exported.mp4");
            var exported = await CallAsync(client, "recording-export", token,
                ("artifactId", latest.ArtifactId), ("destinationPath", downloaded));
            using var exportMetadata = JsonDocument.Parse(Text(exported));
            Assert.Equal(downloaded, exportMetadata.RootElement.GetProperty("Path").GetString());
            Assert.Equal(bytesWritten, await File.ReadAllBytesAsync(downloaded, token));
            var sourceAfter = await CallAsync(client, "ui-screenshot", token, ("nodeId", root));
            await File.WriteAllBytesAsync(Path.Combine(runDirectory, "source-after.png"),
                Assert.Single(sourceAfter.Content.OfType<ImageContentBlock>()).DecodedData.ToArray(), token);
        }
        catch (Exception error)
        {
            failure = error.ToString();
            throw;
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                if (latest is { State: "recording" or "starting" or "finalizing" })
                {
                    Read<RecordingStatus>(await CallAsync(client, "recording-stop", cleanup.Token,
                        ("recordingId", latest.RecordingId)));
                    latest = await WaitForStateAsync(client, latest.RecordingId, "completed",
                        TimeSpan.FromSeconds(10), cleanup.Token);
                }
                await CallAsync(client, "detach", cleanup.Token);
            }
            catch (Exception error)
            {
                failure ??= "Cleanup: " + error;
            }
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "manifest.json"), JsonSerializer.Serialize(new
            {
                CapturedAtUtc = DateTimeOffset.UtcNow, FixtureProcessId = fixture.ProcessId,
                FixtureStartTimeUtc = fixture.StartTimeUtc, FixturePipe = fixture.PipeName,
                CliPath = Metadata("PreviewCliPath"), Scope = controlsOnly ? "CounterButton + InteractionStatus" : "Whole window",
                Recording = latest, Actions = actions, FileGrowthBeforeStop = growth, Failure = failure,
                VisualReview = "Required: inspect the preserved MP4 for click rings and correct crop after window movement."
            }, Json), CancellationToken.None);
        }
    }

    [Fact(SkipUnless = nameof(CanRunDesktopRecording),
        Skip = "Set AVDT_RUN_RECORDING_DESKTOP_TESTS=1 on an interactive Windows desktop to measure the owned fixture.")]
    public async Task Stdio_recording_reports_ABBA_click_roundtrip_and_target_CPU_observations()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(130));
        var token = deadline.Token;
        var evidenceRoot = Environment.GetEnvironmentVariable("AVDT_RECORDING_EVIDENCE_DIR") ??
                           Path.Combine(Metadata("PreviewRepositoryPath"), "artifacts", "recording-verification");
        var runDirectory = Path.GetFullPath(Path.Combine(evidenceRoot,
            "performance-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(runDirectory);
        var blocks = new List<PerformanceBlock>();
        RecordingStatus? latest = null;
        string? failure = null;
        var totalClock = Stopwatch.StartNew();
        await using var fixture = await OwnedSample.StartAsync(token);
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = "dotnet", Name = "Owned desktop recording ABBA observation",
            Arguments = [Metadata("PreviewCliPath"), "mcp", "--pipe", fixture.PipeName],
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        }), cancellationToken: token);
        try
        {
            var counter = await FindAsync(client, "CounterButton", token);
            for (var index = 0; index < 10; index++)
                Assert.True(Read<InputResult>(await CallAsync(client, "ui-click", token, ("nodeId", counter))).Delivered);
            await Task.Delay(300, token);

            foreach (var (label, recording) in new[] { ("A1", false), ("B1", true), ("B2", true), ("A2", false) })
            {
                if (recording)
                {
                    latest = Read<RecordingStatus>(await CallAsync(client, "recording-start", token,
                        ("outputDirectory", Path.Combine(runDirectory, label)), ("frameRate", 30),
                        ("maxDurationSeconds", 30), ("mouseClicks", true)));
                    latest = await WaitForStateAsync(client, latest.RecordingId, "recording", TimeSpan.FromSeconds(15), token);
                    Assert.True(latest.HardwareEncodingVerified,
                        "The recording comparison requires verified hardware encoding: " + JsonSerializer.Serialize(latest, Json));
                }
                // Keep warm-up, recorder initialization and settle time outside the measured block.
                await Task.Delay(400, token);
                using var target = Process.GetProcessById(fixture.ProcessId);
                Assert.Equal(fixture.StartTimeUtc, target.StartTime.ToUniversalTime());
                target.Refresh();
                var cpuBefore = target.TotalProcessorTime;
                using var worker = recording ? Process.GetProcessById(latest!.WorkerProcessId) : null;
                worker?.Refresh();
                var workerCpuBefore = worker?.TotalProcessorTime;
                var blockClock = Stopwatch.StartNew();
                var workerMemory = new List<MemoryObservation>();
                var samples = new double[200];
                for (var index = 0; index < samples.Length; index++)
                {
                    var start = Stopwatch.GetTimestamp();
                    var result = await CallAsync(client, "ui-click", token, ("nodeId", counter));
                    samples[index] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    Assert.True(Read<InputResult>(result).Delivered);
                    if (worker is not null && index % 12 == 0)
                    {
                        worker.Refresh();
                        var observed = Read<RecordingStatus>(await CallAsync(client, "recording-status", token,
                            ("recordingId", latest!.RecordingId)));
                        workerMemory.Add(new MemoryObservation(blockClock.Elapsed.TotalMilliseconds,
                            worker.PrivateMemorySize64, worker.WorkingSet64,
                            observed.ManagedHeapBytes, observed.ManagedAllocatedBytes,
                            observed.Gen0Collections, observed.Gen1Collections, observed.Gen2Collections,
                            observed.EncoderTexturePoolAllocated, observed.EncoderTexturePoolInUse,
                            observed.EncoderTexturePoolReuses));
                    }
                    // Allow normal rendering between actions; pacing is excluded from each round-trip sample.
                    await Task.Delay(75, token);
                }
                blockClock.Stop();
                target.Refresh();
                var cpuMilliseconds = (target.TotalProcessorTime - cpuBefore).TotalMilliseconds;
                worker?.Refresh();
                var workerCpuMilliseconds = worker is not null && workerCpuBefore is { } before
                    ? (worker.TotalProcessorTime - before).TotalMilliseconds : (double?)null;
                var workerPrivateBytes = worker?.PrivateMemorySize64;
                var workerWorkingSetBytes = worker?.WorkingSet64;
                if (recording)
                {
                    Read<RecordingStatus>(await CallAsync(client, "recording-stop", token,
                        ("recordingId", latest!.RecordingId)));
                    latest = await WaitForStateAsync(client, latest!.RecordingId, "completed", TimeSpan.FromSeconds(15), token);
                    Assert.Equal(0, latest.InputMarkersDropped);
                    Assert.True(latest.HardwareEncodingVerified);
                    Assert.True(latest.ManagedMemoryObserved);
                    Assert.True(latest.TexturePoolObserved);
                    Assert.InRange(latest.EncoderTexturePoolAllocated, 1, 8);
                    Assert.InRange(latest.EncoderTexturePoolBytes, 1, 128L * 1024 * 1024);
                    Assert.True(latest.EncoderTexturePoolReuses > 0);
                    Assert.Equal(0, latest.EncoderTexturePoolTimeouts);
                    Assert.Equal(0, latest.EncoderTexturePoolInUse);
                    Assert.True(latest.Width > 0 && latest.Height > 0 && latest.BytesWritten > 1024);
                    Assert.DoesNotContain(latest.Limitations,
                        item => item.Contains("Agent framework input highlights are unavailable", StringComparison.Ordinal));
                }
                blocks.Add(new PerformanceBlock(label, recording, samples,
                    Percentile(samples, 0.50), Percentile(samples, 0.95), Percentile(samples, 0.99),
                    blockClock.Elapsed.TotalMilliseconds, cpuMilliseconds,
                    blockClock.Elapsed.TotalMilliseconds > 0 ? cpuMilliseconds / blockClock.Elapsed.TotalMilliseconds : 0,
                    workerCpuMilliseconds,
                    workerCpuMilliseconds is { } measured ? measured / blockClock.Elapsed.TotalMilliseconds : null,
                    workerPrivateBytes,
                    workerWorkingSetBytes,
                    workerMemory.ToArray(),
                    recording ? latest : null));
            }
            Assert.Equal(4, blocks.Count);
            Assert.Equal(400, blocks.Where(item => item.Recording).Sum(item => item.ClickMilliseconds.Length));
        }
        catch (Exception error)
        {
            failure = error.ToString();
            throw;
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                if (latest is { State: "recording" or "starting" or "finalizing" })
                {
                    await CallAsync(client, "recording-stop", cleanup.Token, ("recordingId", latest.RecordingId));
                    latest = await WaitForStateAsync(client, latest.RecordingId, "completed", TimeSpan.FromSeconds(10), cleanup.Token);
                }
                await CallAsync(client, "detach", cleanup.Token);
            }
            catch (Exception error) { failure ??= "Cleanup: " + error; }
            var baseline = blocks.Where(item => !item.Recording).SelectMany(item => item.ClickMilliseconds).ToArray();
            var recorded = blocks.Where(item => item.Recording).SelectMany(item => item.ClickMilliseconds).ToArray();
            await File.WriteAllTextAsync(Path.Combine(runDirectory, "performance.json"), JsonSerializer.Serialize(new
            {
                CapturedAtUtc = DateTimeOffset.UtcNow, Order = "ABBA", WarmupClicks = 10, ClicksPerBlock = 200,
                InterActionPacingMilliseconds = 75,
                Metric = "stdio ui-click round-trip until InputResult; not input-to-photon latency or target frame time",
                QuantileMethod = "Linear interpolation at (n-1)*p; 400 samples per condition in this short observation",
                CpuUnit = "Target CPU milliseconds and CPU time / wall time, where 1.0 means one logical core fully used",
                WorkerCpu = "Recorder process CPU time / block wall time; one logical core is 1.0",
                ProcessId = fixture.ProcessId, ProcessStartTimeUtc = fixture.StartTimeUtc,
                LogicalProcessors = Environment.ProcessorCount, Runtime = Environment.Version.ToString(),
                OperatingSystem = RuntimeInformation.OSDescription, CliPath = Metadata("PreviewCliPath"),
                TotalSeconds = totalClock.Elapsed.TotalSeconds, Blocks = blocks,
                Baseline = Summarize(baseline), Recording = Summarize(recorded),
                P95DeltaMilliseconds = baseline.Length > 0 && recorded.Length > 0
                    ? Percentile(recorded, 0.95) - Percentile(baseline, 0.95) : (double?)null,
                Failure = failure,
                Interpretation = "Functional and hardware-encoding assertions are checked. Timing is an observation without a fixed pass threshold; repeat on representative scenes and hardware before making a low-interference claim."
            }, Json), CancellationToken.None);
        }
    }

    [Fact(SkipUnless = nameof(CanRunDesktopRecording),
        Skip = "Set AVDT_RUN_RECORDING_DESKTOP_TESTS=1 on an interactive Windows desktop.")]
    public async Task Cancelled_stop_can_be_retried_without_poisoning_the_recording()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        await using var fixture = await OwnedSample.StartAsync(timeout.Token);
        using var target = Process.GetProcessById(fixture.ProcessId);
        var output = Path.Combine(Metadata("PreviewRepositoryPath"), "artifacts", "recording-verification",
            "cancel-stop-" + Guid.NewGuid().ToString("N"));
        await using var recorder = await RecordingSession.StartAsync(new RecordingStartRequest
        {
            ProcessId = fixture.ProcessId, StartTimeUtc = fixture.StartTimeUtc,
            WindowHandle = target.MainWindowHandle.ToInt64(), OutputDirectory = output,
            MouseClicks = false, MaxDurationSeconds = 20
        }, new RecordingClientOptions
        {
            WorkerPath = Path.Combine(Path.GetDirectoryName(Metadata("PreviewCliPath"))!,
                "recording-worker", "AvaloniaDevTool.Recording.Worker.dll")
        }, timeout.Token);
        while (recorder.Current.State == "starting")
        {
            await Task.Delay(100, timeout.Token);
            await recorder.ReadStatusAsync(timeout.Token);
        }
        Assert.Equal("recording", recorder.Current.State);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recorder.StopAsync(cancelled.Token));
        Assert.True(recorder.IsActive);
        await recorder.StopAsync(timeout.Token);
        while (recorder.IsActive)
        {
            await Task.Delay(100, timeout.Token);
            await recorder.ReadStatusAsync(timeout.Token);
        }
        Assert.Equal("completed", recorder.Current.State);
    }

    private sealed record PerformanceBlock(string Label, bool Recording, double[] ClickMilliseconds,
        double P50Milliseconds, double P95Milliseconds, double P99Milliseconds,
        double WallMilliseconds, double TargetCpuMilliseconds, double TargetCpuCoreEquivalent,
        double? WorkerCpuMilliseconds, double? WorkerCpuCoreEquivalent, long? WorkerPrivateBytes,
        long? WorkerWorkingSetBytes,
        MemoryObservation[] WorkerMemory,
        RecordingStatus? Video);

    private sealed record MemoryObservation(double Milliseconds, long PrivateBytes, long WorkingSetBytes,
        long ManagedHeapBytes, long ManagedAllocatedBytes, int Gen0, int Gen1, int Gen2,
        int PoolAllocated, int PoolInUse, long PoolReuses);

    private static object Summarize(double[] samples) => new
    {
        Count = samples.Length,
        P50Milliseconds = samples.Length > 0 ? Percentile(samples, 0.50) : (double?)null,
        P95Milliseconds = samples.Length > 0 ? Percentile(samples, 0.95) : (double?)null,
        P99Milliseconds = samples.Length > 0 ? Percentile(samples, 0.99) : (double?)null
    };

    private static double Percentile(double[] samples, double percentile)
    {
        var sorted = samples.Order().ToArray();
        var position = (sorted.Length - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static async Task<RecordingStatus> WaitForStateAsync(McpClient client, string id, string expected,
        TimeSpan timeout, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        RecordingStatus? latest = null;
        while (clock.Elapsed < timeout)
        {
            latest = Read<RecordingStatus>(await CallAsync(client, "recording-status", token, ("recordingId", id)));
            Assert.True(latest.State != "failed", JsonSerializer.Serialize(latest, Json));
            if (latest.State == expected)
                return latest;
            await Task.Delay(150, token);
        }
        throw new TimeoutException($"Recording did not reach {expected}: {JsonSerializer.Serialize(latest, Json)}");
    }

    private static async Task<long> FindAsync(McpClient client, string name, CancellationToken token) =>
        Assert.Single(Read<TreeSearchResult>(await CallAsync(client, "search", token, ("query", name))).Matches,
            item => item.Name == name).Id;

    private static async Task<CallToolResult> CallAsync(McpClient client, string name, CancellationToken token,
        params (string Name, object? Value)[] arguments)
    {
        var result = await client.CallToolAsync(name, arguments.ToDictionary(item => item.Name, item => item.Value),
            cancellationToken: token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), token);
        Assert.False(result.IsError == true, name + ": " + Text(result));
        return result;
    }

    private static T Read<T>(CallToolResult result) => JsonSerializer.Deserialize<T>(
        Assert.Single(result.Content.OfType<TextContentBlock>()).Text, Json)!;

    private static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text));
    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
    private static string Metadata(string key) => typeof(RecordingDesktopMcpTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(item => item.Key == key).Value!;

    private sealed class OwnedSample : IAsyncDisposable
    {
        private const string Prefix = "avdt-recording-desktop-";
        private readonly Process _process;
        private readonly string _directory;
        private readonly Task<string> _output;
        private readonly Task<string> _errors;

        private OwnedSample(Process process, string directory, string pipe)
        {
            _process = process;
            _directory = directory;
            PipeName = pipe;
            StartTimeUtc = process.StartTime.ToUniversalTime();
            _output = process.StandardOutput.ReadToEndAsync();
            _errors = process.StandardError.ReadToEndAsync();
        }

        public string PipeName { get; }
        public int ProcessId => _process.Id;
        public DateTime StartTimeUtc { get; }

        public static async Task<OwnedSample> StartAsync(CancellationToken token)
        {
            var directory = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var pipe = DiagnosticsProtocol.PipePrefix + "recording-" + Guid.NewGuid().ToString("N");
            var ready = Path.Combine(directory, "ready.txt");
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in new[] { Metadata("AutomationSamplePath"), "--pipe", pipe,
                         "--test-root", directory, "--ready-file", ready })
                start.ArgumentList.Add(argument);
            var owned = new OwnedSample(Process.Start(start) ?? throw new InvalidOperationException("The owned recording sample did not start."), directory, pipe);
            try
            {
                var clock = Stopwatch.StartNew();
                while (!File.Exists(ready) && clock.Elapsed < TimeSpan.FromSeconds(20))
                {
                    Assert.False(owned._process.HasExited, "The owned sample exited before readiness.");
                    await Task.Delay(50, token);
                }
                Assert.True(File.Exists(ready), "The owned sample did not become ready.");
                Assert.Contains($"pid={owned.ProcessId}\n", await File.ReadAllTextAsync(ready, token));
                return owned;
            }
            catch
            {
                await owned.DisposeAsync();
                throw;
            }
        }

        public object MoveWindow(int deltaX, int deltaY)
        {
            VerifyIdentity();
            _process.Refresh();
            var window = _process.MainWindowHandle;
            Assert.NotEqual(IntPtr.Zero, window);
            GetWindowThreadProcessId(window, out var processId);
            Assert.Equal(ProcessId, (int)processId);
            Assert.True(GetWindowRect(window, out var rect));
            Assert.True(SetWindowPos(window, IntPtr.Zero, rect.Left + deltaX, rect.Top + deltaY, 0, 0,
                0x0001 | 0x0004 | 0x0010)); // Preserve size, z-order and activation.
            return new { rect.Left, rect.Top, rect.Right, rect.Bottom };
        }

        private void VerifyIdentity()
        {
            Assert.False(_process.HasExited);
            Assert.Equal(StartTimeUtc, _process.StartTime.ToUniversalTime());
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    VerifyIdentity();
                    _process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _process.WaitForExitAsync(timeout.Token);
                }
                await Task.WhenAll(_output, _errors).WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                _process.Dispose();
                var resolved = Path.GetFullPath(_directory);
                var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
                if (Path.GetDirectoryName(resolved) != parent || !Path.GetFileName(resolved).StartsWith(Prefix, StringComparison.Ordinal))
                    throw new InvalidOperationException("The recording fixture cleanup path escaped its private temporary directory.");
                Directory.Delete(resolved, recursive: true);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out WindowRect rect);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    }
}

[CollectionDefinition("Recording desktop integration", DisableParallelization = true)]
public sealed class RecordingDesktopTestCollection;
