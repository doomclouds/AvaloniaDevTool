using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace AvaloniaDevTool.Backends.XamlMcp.Tests;

internal sealed class TargetFixture : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;
    private readonly ITestOutputHelper _output;
    public string DirectoryPath { get; }
    public string DiscoveryDirectory { get; }
    public int Pid => _process.Id;
    public string AvaloniaVersion { get; private set; } = "";
    public string AgentVersion { get; private set; } = "";

    private TargetFixture(Process process, string directory, string discovery, ITestOutputHelper output)
    {
        _process = process;
        DirectoryPath = directory;
        DiscoveryDirectory = discovery;
        _output = output;
        _stdout = process.StandardOutput.ReadToEndAsync();
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public static async Task<TargetFixture> StartAsync(ITestOutputHelper output)
    {
        var root = Path.GetFullPath(typeof(TargetFixture).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RepositoryRoot").Value!);
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var target = Path.Combine(root, "samples", "AvaloniaDevTool.XamlMcpTarget", "bin",
            configuration, "net10.0", "AvaloniaDevTool.XamlMcpTarget.dll");
        if (!File.Exists(target)) throw new FileNotFoundException("Build the standalone package target before integration tests.", target);
        var directory = Path.Combine(Path.GetTempPath(), "avdt-xamlmcp-test-" + Guid.NewGuid().ToString("N"));
        var discovery = Path.Combine(directory, "discovery");
        System.IO.Directory.CreateDirectory(discovery);
        var ready = Path.Combine(directory, "ready.json");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(target);
        foreach (var argument in new[] { "--headless", "--enable-inspection", "--discovery-dir", discovery, "--ready-file", ready })
            start.ArgumentList.Add(argument);
        var process = Process.Start(start) ?? throw new InvalidOperationException("The standalone target did not launch.");
        var fixture = new TargetFixture(process, directory, discovery, output);
        output.WriteLine($"Standalone NuGet target PID {process.Id}");
        try
        {
            var until = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < until)
            {
                if (process.HasExited)
                    throw new InvalidOperationException("The target exited: " + await fixture._stderr);
                if (File.Exists(ready))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(ready, Encoding.UTF8));
                        fixture.AvaloniaVersion = document.RootElement.GetProperty("avaloniaVersion").GetString() ?? "";
                        fixture.AgentVersion = document.RootElement.GetProperty("agentPackageVersion").GetString() ?? "";
                        return fixture;
                    }
                    catch (JsonException) { }
                    catch (IOException) { }
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("The standalone target did not write its readiness marker.");
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public void RecordServer(int? pid)
    {
        if (pid is { } value) _output.WriteLine($"Disposed upstream server PID {value}");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
        _output.WriteLine($"Standalone target PID {_process.Id} exited.");
        _process.Dispose();
        await _stdout;
        await _stderr;
        var directory = Path.GetFullPath(DirectoryPath);
        var prefix = Path.Combine(Path.GetTempPath(), "avdt-xamlmcp-test-");
        if (!directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The test cleanup path is outside its owned temporary directory.");
        System.IO.Directory.Delete(directory, recursive: true);
    }
}

