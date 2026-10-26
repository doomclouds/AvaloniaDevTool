using System.Net;
using AvaloniaDevTool.Cli.Hosting.Options;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class McpHostOptionsTests
{
    [Fact]
    public void Default_command_preserves_stdio_agent_without_an_initial_pipe()
    {
        var options = McpHostOptions.Parse(["mcp"]);
        Assert.False(options.ShowHelp);
        Assert.Equal("stdio", options.Transport);
        Assert.Equal("agent", options.Profile);
        Assert.Null(options.PipeName);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("official")]
    [InlineData("extended")]
    public void Stdio_accepts_existing_profiles_and_initial_pipe(string profile)
    {
        var options = McpHostOptions.Parse(["mcp", "--pipe", "test-pipe", "--profile", profile]);
        Assert.Equal("stdio", options.Transport);
        Assert.Equal(profile, options.Profile);
        Assert.Equal("test-pipe", options.PipeName);
    }

    [Fact]
    public void Http_defaults_to_loopback_agent_and_the_fixed_mcp_endpoint()
    {
        var options = McpHostOptions.Parse(["mcp", "--transport", "http"]);
        Assert.Equal(IPAddress.Loopback, options.ListenAddress);
        Assert.Equal(7443, options.Port);
        Assert.Equal("agent", options.Profile);
        Assert.Equal("/mcp", McpHostOptions.EndpointPath);
    }

    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50")]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("010.001.002.003", "10.1.2.3")]
    [InlineData("::1", "::1")]
    [InlineData("::", "::")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("::ffff:192.168.1.50", "::ffff:192.168.1.50")]
    public void Http_accepts_plain_addresses_independently_of_option_order(string listen, string expected)
    {
        var options = McpHostOptions.Parse([
            "mcp", "--listen", listen, "--port", "65535", "--pipe", "test-pipe",
            "--profile", "agent", "--transport", "http"
        ]);
        Assert.Equal(IPAddress.Parse(expected), options.ListenAddress);
        Assert.Equal(65535, options.Port);
        Assert.Equal("test-pipe", options.PipeName);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("http://127.0.0.1")]
    [InlineData("127.0.0.1:7443")]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("256.0.0.1")]
    [InlineData("127..0.1")]
    [InlineData("127.0.0.1 ")]
    [InlineData(" 127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("[::1]:7443")]
    [InlineData("fe80::1%12")]
    [InlineData("::ffff:127.1")]
    public void Http_rejects_hostnames_urls_ports_short_addresses_and_scope_ids(string listen) =>
        Error("invalid_listen", ["mcp", "--transport", "http", "--listen", listen]);

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("+80")]
    [InlineData("80 ")]
    [InlineData(" 80")]
    [InlineData("1.5")]
    [InlineData("0x50")]
    [InlineData("８０")]
    [InlineData("99999999999999999999999999")]
    public void Http_rejects_ports_outside_the_plain_decimal_range(string port) =>
        Error("invalid_port", ["mcp", "--transport", "http", "--port", port]);

    [Theory]
    [InlineData("1", 1)]
    [InlineData("00080", 80)]
    [InlineData("65535", 65535)]
    public void Http_accepts_decimal_port_boundaries(string value, int expected) =>
        Assert.Equal(expected, McpHostOptions.Parse(["mcp", "--transport", "http", "--port", value]).Port);

    [Theory]
    [InlineData("official")]
    [InlineData("extended")]
    public void Http_rejects_known_nonagent_profiles_before_host_creation(string profile) =>
        Error("unsupported_profile", ["mcp", "--profile", profile, "--transport", "http"]);

    [Fact]
    public void Unknown_profile_and_transport_have_distinct_errors()
    {
        Error("invalid_profile", ["mcp", "--transport", "http", "--profile", "unknown"]);
        Error("invalid_transport", ["mcp", "--transport", "websocket"]);
    }

    [Theory]
    [InlineData("--listen", "127.0.0.1")]
    [InlineData("--port", "7443")]
    public void Http_only_options_are_rejected_with_default_or_explicit_stdio(string name, string value)
    {
        Error("invalid_arguments", ["mcp", name, value]);
        Error("invalid_arguments", ["mcp", name, value, "--transport", "stdio"]);
    }

    [Theory]
    [InlineData("--transport", "http")]
    [InlineData("--profile", "agent")]
    [InlineData("--pipe", "test-pipe")]
    [InlineData("--listen", "127.0.0.1")]
    [InlineData("--port", "7443")]
    public void Every_option_rejects_duplicates_missing_values_and_empty_values(string name, string value)
    {
        Error("invalid_arguments", ["mcp", name, value, name, value]);
        Error("invalid_arguments", ["mcp", name]);
        Error("invalid_arguments", ["mcp", name, ""]);
        Error("invalid_arguments", ["mcp", name, " "]);
        Error("invalid_arguments", ["mcp", name, "--profile", "agent"]);
    }

    [Fact]
    public void Help_is_available_without_a_command_and_after_the_mcp_command()
    {
        Assert.True(McpHostOptions.Parse([]).ShowHelp);
        Assert.True(McpHostOptions.Parse(["--help"]).ShowHelp);
        Assert.True(McpHostOptions.Parse(["-h"]).ShowHelp);
        Assert.True(McpHostOptions.Parse(["mcp", "--help"]).ShowHelp);
        Assert.True(McpHostOptions.Parse(["mcp", "-h"]).ShowHelp);
        Assert.Contains("--transport", McpHostOptions.HelpText);
        Assert.Contains("/mcp", McpHostOptions.HelpText);
    }

    [Fact]
    public void Unknown_options_and_positional_arguments_are_rejected()
    {
        Error("invalid_arguments", ["other"]);
        Error("invalid_arguments", ["mcp", "--unknown", "value"]);
        Error("invalid_arguments", ["mcp", "positional", "value"]);
        Error("invalid_arguments", ["mcp", "--help", "--unknown", "value"]);
    }

    private static void Error(string code, string[] args)
    {
        var error = Assert.Throws<McpHostOptionsException>(() => McpHostOptions.Parse(args));
        Assert.Equal(code, error.Code);
        Assert.StartsWith(code + ": ", error.Message);
    }
}
