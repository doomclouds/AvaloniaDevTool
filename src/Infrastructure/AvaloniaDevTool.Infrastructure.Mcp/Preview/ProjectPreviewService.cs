using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using ModelContextProtocol;

namespace AvaloniaDevTool.Cli;

internal static class ProjectPreviewService
{
    public static async Task<PreparedPreview> PrepareAsync(string xamlFilePath,
        string xamlProjectPath,
        bool build,
        CancellationToken cancellationToken)
    {
        xamlFilePath = ProjectPreview.AbsoluteFile(xamlFilePath, nameof(xamlFilePath));
        xamlProjectPath = ProjectPreview.AbsoluteFile(xamlProjectPath, nameof(xamlProjectPath));
        var relative = Path.GetRelativePath(Path.GetDirectoryName(xamlProjectPath)!, xamlFilePath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.IsPathRooted(relative))
            throw new McpException(
                "source_outside_project: xamlFilePath must be inside the selected project's directory.");
        if (!string.Equals(Path.GetExtension(xamlProjectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
            throw new McpException("invalid_project: xamlProjectPath must select a C# project file.");

        var project = await ProjectPreview.ResolveAsync(xamlProjectPath, cancellationToken).ConfigureAwait(false);
        if (build)
            await ProjectPreview.BuildAsync(project, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(project.AssemblyPath))
            throw new McpException(
                $"project_not_built: The selected {project.Framework} project output does not exist: {project.AssemblyPath}. Build the project or use build=true.");

        var headless = string.Equals(Environment.GetEnvironmentVariable("AVDT_PREVIEW_HEADLESS"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        return new PreparedPreview(xamlFilePath, project.AssemblyPath, project.ProjectPath, headless);
    }
}

internal sealed record PreparedPreview(string FilePath, string AssemblyPath, string ProjectPath, bool Headless);

internal sealed record ProjectPreviewContext(string ProjectPath, string Framework, string AssemblyPath);

internal static class ProjectPreview
{
    internal static string AbsoluteFile(string path, string name)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new McpException($"invalid_path: {name} must be an absolute file path.");
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new McpException($"file_not_found: {name} does not exist: {path}.");
        return path;
    }

    internal static async Task<ProjectPreviewContext> ResolveAsync(string projectPath, CancellationToken token)
    {
        var outer = await EvaluateAsync(projectPath, null, token).ConfigureAwait(false);
        var names = Value(outer, "TargetFrameworks")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0 && Value(outer, "TargetFramework") is { Length: > 0 } single)
            names = [single];
        if (names.Length == 0)
            throw new McpException(
                "unsupported_project: The project does not declare TargetFramework or TargetFrameworks.");

        var candidates =
            new List<(string Name, Version Version, bool Platform, Dictionary<string, string> Properties)>();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var properties = names.Length == 1 && Value(outer, "TargetFramework") == name
                ? outer
                : await EvaluateAsync(projectPath, name, token).ConfigureAwait(false);
            var identifier = Value(properties, "TargetFrameworkIdentifier");
            if (!Version.TryParse(Value(properties, "TargetFrameworkVersion").TrimStart('v', 'V'), out var version))
                continue;
            if (identifier == ".NETCoreApp" &&
                version > new Version(Environment.Version.Major, Environment.Version.Minor))
                continue;
            if (identifier == ".NETStandard" && version > new Version(2, 1))
                continue;
            if (identifier is not (".NETCoreApp" or ".NETStandard"))
                continue;
            var platform = Value(properties, "TargetPlatformIdentifier");
            if (platform.Length != 0 && !(OperatingSystem.IsWindows() &&
                                          platform.Equals("Windows", StringComparison.OrdinalIgnoreCase)))
                continue;
            candidates.Add((name, version, platform.Length != 0, properties));
        }

        var selected = candidates
            .OrderByDescending(item => Value(item.Properties, "TargetFrameworkIdentifier") == ".NETCoreApp")
            .ThenByDescending(item => item.Version).ThenBy(item => item.Platform)
            .ThenBy(item => item.Name, StringComparer.Ordinal).FirstOrDefault();
        if (selected.Properties is null)
            throw new McpException(
                $"unsupported_target_framework: No target framework in '{string.Join(';', names)}' can load in this .NET {Environment.Version.Major} preview host.");
        var assembly = Value(selected.Properties, "TargetPath");
        if (string.IsNullOrWhiteSpace(assembly))
            throw new McpException(
                "invalid_project_output: MSBuild did not evaluate a TargetPath for the selected project.");
        if (!Path.IsPathFullyQualified(assembly))
            assembly = Path.Combine(Path.GetDirectoryName(projectPath)!, assembly);
        assembly = Path.GetFullPath(assembly);
        if (!string.Equals(Path.GetExtension(assembly), ".dll", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(assembly), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new McpException(
                $"unsupported_project_output: The project TargetPath is not a managed assembly: {assembly}.");
        return new ProjectPreviewContext(projectPath, selected.Name, assembly);
    }

    internal static async Task BuildAsync(ProjectPreviewContext context, CancellationToken token)
    {
        var result = await RunAsync(context.ProjectPath,
            [
                "build", context.ProjectPath, "--framework", context.Framework, "--nologo", "-nodeReuse:false",
                "-property:UseSharedCompilation=false"
            ],
            token).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new McpException(
                $"project_build_failed: Build failed for {context.Framework} (exit {result.ExitCode}). {result.Output}");
    }

    private static async Task<Dictionary<string, string>> EvaluateAsync(string projectPath,
        string? framework,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var arguments = new List<string>
        {
            "msbuild", projectPath, "-nologo", "-nodeReuse:false",
            "-getProperty:TargetFramework,TargetFrameworks,TargetPath,TargetFrameworkIdentifier,TargetFrameworkVersion,TargetPlatformIdentifier"
        };
        if (framework is not null)
            arguments.Add("-property:TargetFramework=" + framework);
        ProjectCommandResult result;
        try
        {
            result = await RunAsync(projectPath, arguments, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new McpException(
                "project_evaluation_timeout: MSBuild project evaluation exceeded 30 seconds; its owned process was stopped.");
        }

        if (result.ExitCode != 0)
            throw new McpException(
                $"project_evaluation_failed: MSBuild evaluation failed (exit {result.ExitCode}). {result.Output}");
        try
        {
            using var json = JsonDocument.Parse(result.StandardOutput);
            return json.RootElement.GetProperty("Properties").EnumerateObject()
                .ToDictionary(property => property.Name,
                    property => property.Value.GetString() ?? "",
                    StringComparer.Ordinal);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new McpException(
                $"invalid_project_evaluation: MSBuild did not return evaluated project properties. {result.Output}");
        }
    }

    private static string Value(Dictionary<string, string> properties, string name) =>
        properties.GetValueOrDefault(name, "");

    private static async Task<ProjectCommandResult> RunAsync(string projectPath,
        IEnumerable<string> arguments,
        CancellationToken token)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(projectPath)!
        };
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        using var job = OwnedBuildJob.Create();
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new McpException("project_command_failed: Could not start the .NET SDK.");
        }
        catch (Win32Exception error)
        {
            throw new McpException(
                $"dotnet_not_found: A .NET SDK is required to evaluate and optionally build the project. {error.Message}");
        }

        var output = ReadBoundedAsync(process.StandardOutput, token);
        var errorOutput = ReadBoundedAsync(process.StandardError, token);
        try
        {
            job?.Assign(process);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            // A custom build target can exit after spawning a child that still owns these pipes.
            // End all owned descendants before waiting for EOF, including after a successful build.
            job?.Dispose();
            var stdout = await output.ConfigureAwait(false);
            var stderr = await errorOutput.ConfigureAwait(false);
            return new ProjectCommandResult(process.ExitCode,
                stdout,
                string.Join('\n', new[] { stdout, stderr }.Where(value => value.Length != 0)));
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Win32Exception) when (process.HasExited)
                {
                }

                await process.WaitForExitAsync().ConfigureAwait(false);
            }

            job?.Dispose();
            await Task.WhenAll(output, errorOutput).ConfigureAwait(false);
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        const int limit = 32768;
        var result = new StringBuilder();
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false) is var count && count > 0)
        {
            if (result.Length + count > limit)
                result.Remove(0, Math.Min(result.Length, result.Length + count - limit));
            result.Append(buffer, 0, count);
        }

        return result.ToString();
    }

    private sealed record ProjectCommandResult(int ExitCode, string StandardOutput, string Output);

    // Closing the job handle also kills builds if the stdio owner exits abruptly on Windows.
    private sealed class OwnedBuildJob : SafeHandleZeroOrMinusOneIsInvalid
    {
        private OwnedBuildJob() : base(ownsHandle: true)
        {
        }

        internal static OwnedBuildJob? Create()
        {
            if (!OperatingSystem.IsWindows())
                return null;
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the owned build job.");
            var limits = new ExtendedLimit { BasicLimitInformation = new BasicLimit { LimitFlags = 0x2000 } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>()))
            {
                var error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error, "Could not configure the owned build job.");
            }

            return job;
        }

        internal void Assign(Process process)
        {
            if (!AssignProcessToJobObject(this, process.Handle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not own the project command process.");
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimit
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount,
                WriteOperationCount,
                OtherOperationCount,
                ReadTransferCount,
                WriteTransferCount,
                OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimit
        {
            public BasicLimit BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern OwnedBuildJob CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(OwnedBuildJob job,
            int informationClass,
            ref ExtendedLimit information,
            uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(OwnedBuildJob job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
