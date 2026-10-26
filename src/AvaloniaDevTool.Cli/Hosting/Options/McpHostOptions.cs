using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace AvaloniaDevTool.Cli.Hosting.Options;

public sealed record McpHostOptions
{
    public const int DefaultHttpPort = 7443;
    public const string EndpointPath = "/mcp";

    public bool ShowHelp { get; init; }
    public string Transport { get; init; } = "stdio";
    public string? PipeName { get; init; }
    public string Profile { get; init; } = "agent";
    public IPAddress ListenAddress { get; init; } = IPAddress.Loopback;
    public int Port { get; init; } = DefaultHttpPort;

    public static string HelpText => """
        AvaloniaDevTool.Cli mcp [--transport stdio|http] [--pipe NAME] [--profile agent|official|extended]
                                [--listen IP] [--port PORT]
        Starts the MCP server. Transport defaults to stdio; use attach-to-app to choose a local target.
        HTTP supports the agent profile at /mcp; defaults to 127.0.0.1:7443.
        --listen accepts a plain IPv4 or IPv6 address; --port accepts 1..65535. Both require HTTP.
        Example: AvaloniaDevTool.Cli mcp --transport http --listen 192.168.1.50 --port 7443
        """;

    public static McpHostOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || args.Length == 1 && IsHelp(args[0]) ||
            args.Length == 2 && args[0] == "mcp" && IsHelp(args[1]))
            return new McpHostOptions { ShowHelp = true };
        if (args[0] != "mcp")
            throw Error("invalid_arguments", "Expected the mcp command. Use --help for usage.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            var option = args[index];
            if (option is not ("--transport" or "--pipe" or "--profile" or "--listen" or "--port"))
                throw Error("invalid_arguments", $"Unknown option '{option}'.");
            if (values.ContainsKey(option))
                throw Error("invalid_arguments", $"Option '{option}' may appear only once.");
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]) ||
                args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw Error("invalid_arguments", $"Option '{option}' requires one nonempty value.");
            values.Add(option, args[index + 1]);
        }

        var transport = values.GetValueOrDefault("--transport", "stdio");
        if (transport is not ("stdio" or "http"))
            throw Error("invalid_transport", "Choose stdio or http.");
        var profile = values.GetValueOrDefault("--profile", "agent");
        if (profile is not ("agent" or "official" or "extended"))
            throw Error("invalid_profile", "Choose agent, official or extended.");
        if (transport == "stdio" && (values.ContainsKey("--listen") || values.ContainsKey("--port")))
            throw Error("invalid_arguments", "--listen and --port require --transport http.");
        if (transport == "http" && profile != "agent")
            throw Error("unsupported_profile", "HTTP currently supports only the agent profile.");

        var listenAddress = values.TryGetValue("--listen", out var listen)
            ? ParseListenAddress(listen)
            : IPAddress.Loopback;
        var port = values.TryGetValue("--port", out var portText) ? ParsePort(portText) : DefaultHttpPort;
        return new McpHostOptions
        {
            Transport = transport, PipeName = values.GetValueOrDefault("--pipe"), Profile = profile,
            ListenAddress = listenAddress, Port = port
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
