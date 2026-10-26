using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Cli;

public sealed partial class ToolSession
{
    private readonly Dictionary<string, OfficialApplicationInfo> _applications = new(StringComparer.Ordinal);
    private long _applicationSequence;

    public (IReadOnlyList<OfficialApplicationInfo> Applications, bool Truncated) DiscoverApplications()
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
                    name == _configuredPipe)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(257).ToArray();
            var live = found.ToHashSet(StringComparer.Ordinal);
            foreach (var missing in _applications.Keys.Where(name => !live.Contains(name)).ToArray())
                _applications.Remove(missing);
            foreach (var pipe in found.Take(256))
                if (!_applications.ContainsKey(pipe))
                {
                    var suffix = pipe.StartsWith(DiagnosticsProtocol.PipePrefix, StringComparison.Ordinal)
                        ? pipe[DiagnosticsProtocol.PipePrefix.Length..]
                        : "";
                    int? processId = int.TryParse(suffix, out var parsed) && parsed > 0 ? parsed : null;
                    _applications[pipe] = new(++_applicationSequence, pipe, pipe, processId);
                }

            return (_applications.Values.OrderBy(app => app.Id).ToArray(), found.Length > 256);
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
            _applications[pipe] = current with { Name = info.ApplicationName, ProcessId = info.ProcessId };
    }
}
