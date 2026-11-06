using AvaloniaDevTool.Protocol;
using AvaloniaDevTool.Client.Attachment;
using System.Diagnostics;

namespace AvaloniaDevTool.Cli;

internal sealed class ApplicationDiscovery(string? configuredPipe)
{
    private readonly Dictionary<string, ApplicationDescriptor> _applications = new(StringComparer.Ordinal);
    private long _applicationSequence;

    internal void Clear() => _applications.Clear();

    public (IReadOnlyList<ApplicationDescriptor> Applications, bool Truncated) DiscoverApplications()
    {
        IEnumerable<string> names;
        try
        {
            names = OperatingSystem.IsWindows()
                ? Directory.EnumerateFiles(@"\\.\pipe\").Select(Path.GetFileName).OfType<string>()
                : Directory.EnumerateFiles(Path.GetTempPath(), "CoreFxPipe_*")
                    .Select(Path.GetFileName).OfType<string>().Select(name => name["CoreFxPipe_".Length..]);
            var found = names.Where(name =>
                    name.StartsWith(DiagnosticsProtocol.PipePrefix, StringComparison.Ordinal) ||
                    name == configuredPipe)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(257).ToArray();
            var candidates = WindowsProcessInventory.Discover().Take(257).ToArray();
            var live = found.Concat(candidates.Select(process => DiagnosticsProtocol.PipePrefix + process.ProcessId))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var missing in _applications.Keys.Where(name => !live.Contains(name)).ToArray())
                _applications.Remove(missing);
            foreach (var pipe in live.Order(StringComparer.Ordinal).Take(256))
                {
                    var suffix = pipe.StartsWith(DiagnosticsProtocol.PipePrefix, StringComparison.Ordinal)
                        ? pipe[DiagnosticsProtocol.PipePrefix.Length..]
                        : "";
                    int? processId = int.TryParse(suffix, out var parsed) && parsed > 0 ? parsed : null;
                    var candidate = candidates.FirstOrDefault(process => process.ProcessId == processId);
                    DateTimeOffset? start = candidate?.StartTimeUtc;
                    if (start is null && processId is { } pid)
                    {
                        try { using var process = Process.GetProcessById(pid); start = new(process.StartTime.ToUniversalTime()); }
                        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    }
                    if (!_applications.TryGetValue(pipe, out var current) ||
                        current.StartTimeUtc is { } previous && start is { } next && previous != next)
                        current = new(++_applicationSequence, candidate?.Name ?? pipe, pipe, processId, start);
                    _applications[pipe] = current with
                    {
                        StartTimeUtc = start, RequiresInjection = !found.Contains(pipe), RuntimeVersion = candidate?.RuntimeVersion
                    };
                }

            return (_applications.Values.OrderBy(app => app.Id).ToArray(), live.Count > 256);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new ModelContextProtocol.McpException(
                "discovery_failed: Local application endpoints could not be enumerated.");
        }
    }

    public void RememberApplication(string pipe, SessionInfo info)
    {
        if (_applications.TryGetValue(pipe, out var current))
            _applications[pipe] = current with { Name = info.ApplicationName, ProcessId = info.ProcessId, RequiresInjection = false };
    }
}
