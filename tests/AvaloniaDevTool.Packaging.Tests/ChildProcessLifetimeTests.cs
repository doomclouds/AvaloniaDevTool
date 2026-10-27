using System.Diagnostics;
using AvaloniaDevTool.Packaging;
using Xunit;

namespace AvaloniaDevTool.Packaging.Tests;

public sealed class ChildProcessLifetimeTests
{
    [Fact]
    public async Task An_already_exited_command_keeps_its_successful_exit_code()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("exit 0");
        using var child = Process.Start(start)!;
        await child.WaitForExitAsync();
        using var lifetime = new ChildProcessLifetime(child);
        Assert.Equal(0, child.ExitCode);
    }

    [Fact]
    public async Task Closing_the_owner_job_ends_the_running_child()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "ping.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("30");
        start.ArgumentList.Add("127.0.0.1");
        using var child = Process.Start(start)!;
        try
        {
            using (var lifetime = new ChildProcessLifetime(child))
                Assert.False(child.HasExited);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync(timeout.Token);
            Assert.True(child.HasExited);
        }
        finally
        {
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
        }
    }
}
