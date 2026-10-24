using AvaloniaDevTool.Backends.XamlMcp;
using AvaloniaDevTool.Client;

namespace AvaloniaDevTool.Inspector;

/// <summary>Chooses an immutable backend at process launch; server paths are always explicit.</summary>
public sealed record InspectorLaunchOptions(string Backend = "native", string? InitialEndpoint = null, string? ServerPath = null,
    string? TokenEnvironmentVariable = null)
{
    public static InspectorLaunchOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var key = args[index];
            if (key is not ("--backend" or "--pipe" or "--endpoint" or "--token-env" or "--target" or "--server"))
                throw new ArgumentException("Unknown Inspector option.");
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("A value is required after " + key);
            if (!values.TryAdd(key, args[++index]))
                throw new ArgumentException("The option was specified more than once: " + key);
        }

        var backend = values.GetValueOrDefault("--backend", "native").ToLowerInvariant();
        if (backend == "native")
        {
            if (values.ContainsKey("--target") || values.ContainsKey("--server"))
                throw new ArgumentException("The native backend uses --pipe; --target and --server belong to xamlmcp.");
            if (values.ContainsKey("--pipe") && values.ContainsKey("--endpoint"))
                throw new ArgumentException("Specify only one of --pipe or --endpoint.");
            var endpoint = values.GetValueOrDefault("--endpoint") ?? values.GetValueOrDefault("--pipe");
            var tokenVariable = values.GetValueOrDefault("--token-env");
            if (values.ContainsKey("--endpoint") && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss")))
                throw new ArgumentException("--endpoint requires an absolute ws or wss address.");
            if (tokenVariable is not null && !ValidEnvironmentName(tokenVariable))
                throw new ArgumentException("--token-env requires a valid environment variable name.");
            return new InspectorLaunchOptions(backend, endpoint, TokenEnvironmentVariable: tokenVariable);
        }
        if (backend != "xamlmcp")
            throw new ArgumentException("Supported backends are native and xamlmcp.");
        if (values.ContainsKey("--pipe") || values.ContainsKey("--endpoint") || values.ContainsKey("--token-env"))
            throw new ArgumentException("Use --target with the xamlmcp backend.");
        if (!values.TryGetValue("--server", out var server) || !Path.IsPathFullyQualified(server) || !File.Exists(server))
            throw new ArgumentException("The xamlmcp backend requires --server with an existing absolute Server.dll or Server.exe path.");
        if (Path.GetExtension(server).ToLowerInvariant() is not (".dll" or ".exe"))
            throw new ArgumentException("The explicit XamlMcp server must be a .dll or .exe.");
        if (!values.TryGetValue("--target", out var target) || string.IsNullOrWhiteSpace(target) ||
            target.Length > 512 || target.Any(char.IsControl))
            throw new ArgumentException("The xamlmcp backend requires a target PID or instance identifier.");
        target = target.Trim();
        if (target.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
            target = NormalizePid(target[4..]);
        else if (target.StartsWith("instance:", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(target[9..])) throw new ArgumentException("The instance identifier is empty.");
            target = "instance:" + target[9..];
        }
        else if (target.All(char.IsDigit))
            target = NormalizePid(target);
        else
            target = "instance:" + target;
        return new InspectorLaunchOptions(backend, target, Path.GetFullPath(server));
    }

    public string? ReadRemoteToken() => TokenEnvironmentVariable is { } name ? Environment.GetEnvironmentVariable(name) : null;

    private static bool ValidEnvironmentName(string name) => name.Length is > 0 and <= 128 &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    public IInspectionBackend? CreateBackend()
    {
        if (Backend == "native") return null;
        if (Backend != "xamlmcp" || ServerPath is null || !Path.IsPathFullyQualified(ServerPath) || !File.Exists(ServerPath))
            throw new ArgumentException("Configure an explicit existing XamlMcp server path.");
        var managed = string.Equals(Path.GetExtension(ServerPath), ".dll", StringComparison.OrdinalIgnoreCase);
        return new XamlMcpBackend(new XamlMcpBackendOptions
        {
            ServerCommand = managed ? "dotnet" : ServerPath,
            ServerArguments = managed ? [ServerPath] : [],
            WorkingDirectory = Path.GetDirectoryName(ServerPath)
        });
    }

    private static string NormalizePid(string text) =>
        int.TryParse(text, out var pid) && pid > 0
            ? "pid:" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : throw new ArgumentException("Target PID must be a positive integer.");
}

