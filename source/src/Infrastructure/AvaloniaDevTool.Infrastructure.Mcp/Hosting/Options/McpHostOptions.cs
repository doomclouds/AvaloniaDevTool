using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AvaloniaDevTool.Cli.Hosting.Options;

public sealed record McpHostOptions
{
    public const int DefaultHttpPort = 7443;
    public const string EndpointPath = "/mcp";
    public bool ShowHelp { get; init; }
    public string Role { get; init; } = "mcp";
    public string? PipeName { get; init; }
    public IPAddress ListenAddress { get; init; } = IPAddress.Any;
    public int Port { get; init; } = DefaultHttpPort;

    public static string HelpText => """
        AvaloniaDevTool mcp [--pipe NAME]
          Starts the stdio MCP gateway. select-machine chooses local execution or an IP's HTTP executor.
        AvaloniaDevTool serve [--listen IP] [--port PORT] [--pipe NAME]
          Starts the direct execution server at http://0.0.0.0:7443/mcp by default.
          --listen accepts a plain IPv4 or IPv6 address. --port accepts 1..65535.
        """;

    public static McpHostOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args.Length == 1 && IsHelp(args[0]) ||
            args.Length == 2 && args[0] is "mcp" or "serve" && IsHelp(args[1]))
            return new McpHostOptions { ShowHelp = true };
        if (args[0] is not ("mcp" or "serve"))
            throw Error("invalid_arguments", "Expected mcp or serve. Use --help for usage.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            var option = args[index];
            if (option is not ("--pipe" or "--listen" or "--port"))
                throw Error("invalid_arguments", $"Unknown option '{option}'.");
            if (args[0] == "mcp" && option != "--pipe")
                throw Error("invalid_arguments", $"{option} belongs to serve; the mcp gateway uses stdio.");
            if (values.ContainsKey(option))
                throw Error("invalid_arguments", $"Option '{option}' may appear only once.");
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw Error("invalid_arguments", $"Option '{option}' requires one nonempty value.");
            values.Add(option, args[index + 1]);
        }

        return new McpHostOptions
        {
            Role = args[0], PipeName = values.GetValueOrDefault("--pipe"),
            ListenAddress = values.TryGetValue("--listen", out var listen) ? ParseListenAddress(listen) : IPAddress.Any,
            Port = values.TryGetValue("--port", out var port) ? ParsePort(port) : DefaultHttpPort
        };
    }

    private static bool IsHelp(string value) => value is "--help" or "-h";

    private static IPAddress ParseListenAddress(string value)
    {
        if (value.Any(char.IsWhiteSpace) || value.IndexOfAny(['[', ']', '%']) >= 0)
            throw Error("invalid_listen", "Use a plain IPv4 or IPv6 address without brackets, scope ID or whitespace.");
        if (!value.Contains(':'))
            return TryParseIpv4(value, out var ipv4)
                ? ipv4!
                : throw Error("invalid_listen", "IPv4 must contain four decimal octets in the range 0..255.");

        // Normalize embedded IPv4 ourselves so IPAddress cannot interpret octets as octal or shorthand.
        if (value.Contains('.'))
        {
            var prefixLength = value.LastIndexOf(':') + 1;
            if (!TryParseIpv4(value[prefixLength..], out var embedded))
                throw Error("invalid_listen", "An embedded IPv4 address must contain four decimal octets.");
            value = value[..prefixLength] + embedded;
        }

        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetworkV6)
            throw Error("invalid_listen", "Use a plain IPv4 or IPv6 address without a URL or port.");
        return address;
    }

    private static bool TryParseIpv4(string value, out IPAddress? address)
    {
        address = null;
        var parts = value.Split('.');
        if (parts.Length != 4)
            return false;
        var octets = new byte[4];
        for (var index = 0; index < parts.Length; index++)
            if (parts[index].Length is < 1 or > 3 || !parts[index].All(char.IsAsciiDigit) ||
                !byte.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out octets[index]))
                return false;
        address = new IPAddress(octets);
        return true;
    }

    private static int ParsePort(string value)
    {
        if (!value.All(char.IsAsciiDigit) ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
            throw Error("invalid_port",
                "Port must be a decimal integer in the range 1..65535 without signs or whitespace.");
        return port;
    }

    private static McpHostOptionsException Error(string code, string message) => new(code, message);
}

public sealed class McpHostOptionsException(string code, string message) : ArgumentException($"{code}: {message}")
{
    public string Code { get; } = code;
}
