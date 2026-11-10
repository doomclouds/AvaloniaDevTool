using System.Diagnostics;

namespace AvaloniaDevTool.Client.Attachment;

public sealed record CoreClrProcess(int ProcessId, DateTimeOffset StartTimeUtc, string Name, string RuntimeVersion);

public static class WindowsProcessInventory
{
    public static IReadOnlyList<CoreClrProcess> Discover()
    {
        if (!OperatingSystem.IsWindows())
            return [];
        var found = new List<CoreClrProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero)
                        continue;
                    var runtime = process.Modules.Cast<ProcessModule>().FirstOrDefault(module =>
                        module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
                    if (runtime is null)
                        continue;
                    var title = process.MainWindowTitle;
                    found.Add(new(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime()),
                        string.IsNullOrWhiteSpace(title) ? process.ProcessName : title,
                        runtime.FileVersionInfo.FileVersion ?? "unknown"));
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Inaccessible or exiting processes are not attach candidates.
                }
            }
        }
        return found;
    }
}
