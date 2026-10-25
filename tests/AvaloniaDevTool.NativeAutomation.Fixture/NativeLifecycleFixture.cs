using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class NativeLifecycleFixture
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string ConfigurationFile = "native-lifecycle-fixture.json";

    [ModuleInitializer]
    internal static void Initialize()
    {
        var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var configurationPath = Path.Combine(AppContext.BaseDirectory, ConfigurationFile);
        if (arguments.FirstOrDefault()?.StartsWith("--native-lifecycle-", StringComparison.Ordinal) != true &&
            !File.Exists(configurationPath)) return;

        try
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = new UTF8Encoding(false);
            if (arguments.FirstOrDefault() == "--native-lifecycle-target")
            {
                WriteLine(Identity(Process.GetCurrentProcess()));
                Thread.Sleep(Timeout.Infinite);
            }
            else if (arguments.FirstOrDefault() == "--native-lifecycle-host") RunHost(arguments);
            else RunBlockingWorker(configurationPath);
            Environment.Exit(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            Environment.Exit(1);
        }
    }

    private static void RunBlockingWorker(string configurationPath)
    {
        var configuration = JsonSerializer.Deserialize<WorkerConfiguration>(File.ReadAllText(configurationPath, Encoding.UTF8), JsonOptions)
            ?? throw new InvalidOperationException("A lifecycle worker configuration is required.");
        var directory = Path.GetFullPath(configuration.ControlDirectory);
        if (!string.Equals(directory, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Lifecycle control files must stay inside the worker's directory.");

        while (Console.ReadLine() is { } line)
        {
            using var request = JsonDocument.Parse(line);
            var id = request.RootElement.GetProperty("id").GetInt64();
            var command = request.RootElement.GetProperty("command").GetString();
            if (command == "attach")
            {
                WriteState(Path.Combine(directory, "attached.json"), Identity(Process.GetCurrentProcess()));
                WriteLine(new { id, success = true, result = true });
            }
            else
            {
                WriteState(Path.Combine(directory, "blocked.json"), new { command, process = Identity(Process.GetCurrentProcess()) });
                // The facade must terminate a provider that never returns from an in-flight request.
                Thread.Sleep(Timeout.Infinite);
            }
        }
    }

    private static void RunHost(string[] arguments)
    {
        if (arguments.Length != 6) throw new ArgumentException("The lifecycle host requires worker, target, and owner identities.");
        var workerPath = Path.GetFullPath(arguments[1]);
        var targetId = int.Parse(arguments[2], CultureInfo.InvariantCulture);
        var targetTicks = long.Parse(arguments[3], CultureInfo.InvariantCulture);
        var ownerId = int.Parse(arguments[4], CultureInfo.InvariantCulture);
        var ownerTicks = long.Parse(arguments[5], CultureInfo.InvariantCulture);
        using var owner = Process.GetProcessById(ownerId);
        if (owner.StartTime.ToUniversalTime().Ticks != ownerTicks)
            throw new InvalidOperationException("The lifecycle test owner changed.");
        using var current = Process.GetCurrentProcess();
        using var worker = Process.Start(new ProcessStartInfo(workerPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(workerPath)!
        }) ?? throw new InvalidOperationException("The lifecycle worker could not start.");
        _ = worker.StandardError.ReadToEndAsync();
        worker.StandardInput.WriteLine(JsonSerializer.Serialize(new
        {
            id = 1, command = "attach", payload = new
            {
                target = new { processId = targetId, startTimeUtc = new DateTimeOffset(targetTicks, TimeSpan.Zero) },
                sessionId = Guid.NewGuid().ToString("N"), parentProcessId = current.Id,
                parentStartTimeUtc = current.StartTime.ToUniversalTime()
            }
        }, JsonOptions));
        worker.StandardInput.Flush();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var line = worker.StandardOutput.ReadLineAsync(timeout.Token).AsTask().GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("The real worker exited before attaching.");
        using var response = JsonDocument.Parse(line);
        if (!response.RootElement.GetProperty("success").GetBoolean())
            throw new InvalidOperationException($"The real worker refused attach: {line}");

        SafeHandle inputHandle = worker.StandardInput.BaseStream switch
        {
            FileStream file => file.SafeFileHandle,
            PipeStream pipe => pipe.SafePipeHandle,
            _ => throw new InvalidOperationException("The worker stdin stream has no duplicable handle.")
        };
        if (!DuplicateHandle(current.Handle, inputHandle, owner.Handle, out var duplicate, 0, false, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // Keep stdin open in the test process so EOF cannot masquerade as parent-watchdog cleanup.
        WriteLine(new { host = Identity(current), worker = Identity(worker), inputHandle = duplicate.ToInt64() });
        Thread.Sleep(Timeout.Infinite);
    }

    private static ProcessIdentity Identity(Process process) => new(process.Id,
        process.StartTime.ToUniversalTime().Ticks, Path.GetFullPath(process.MainModule!.FileName!));

    private static void WriteLine(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        Console.Out.Flush();
    }

    private static void WriteState(string path, object value)
    {
        var pending = path + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(value, JsonOptions), new UTF8Encoding(false));
        File.Move(pending, path);
    }

    private sealed record WorkerConfiguration(string ControlDirectory);
    private sealed record ProcessIdentity(int ProcessId, long StartTimeUtcTicks, string ExecutablePath);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(nint sourceProcess, SafeHandle sourceHandle, nint targetProcess,
        out nint targetHandle, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);
}
