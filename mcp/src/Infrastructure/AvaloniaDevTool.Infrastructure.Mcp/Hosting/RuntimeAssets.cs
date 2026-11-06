using System.Diagnostics;

namespace AvaloniaDevTool.Cli.Hosting;

/// <summary>Resolves executables in both development output and the extracted packaged runtime.</summary>
internal static class RuntimeAssets
{
    internal static string Root => Environment.GetEnvironmentVariable("AVDT_RUNTIME_ROOT") is { Length: > 0 } root
        ? Path.GetFullPath(root)
        : AppContext.BaseDirectory;

    internal static string Resolve(string directory, string assembly)
    {
        var executable = Path.Combine(Root, directory, assembly + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return File.Exists(executable) ? executable : Path.Combine(Root, directory, assembly + ".dll");
    }

    internal static ProcessStartInfo StartInfo(string path)
    {
        var managed = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo(managed
            ? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet"
            : path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (managed)
            info.ArgumentList.Add(path);
        return info;
    }
}
