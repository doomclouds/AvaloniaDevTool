namespace AvaloniaDevTool.Sample;

internal static class SampleInspectorLocator
{
    public static string? Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "AvaloniaDevTool.slnx"))) continue;
#if DEBUG
            const string configuration = "Debug";
#else
            const string configuration = "Release";
#endif
            var executable = "AvaloniaDevTool.Inspector" + (OperatingSystem.IsWindows() ? ".exe" : "");
            var path = Path.Combine(directory.FullName, "src", "AvaloniaDevTool.Inspector", "bin", configuration, "net10.0", executable);
            return File.Exists(path) ? path : null;
        }
        return null;
    }
}

