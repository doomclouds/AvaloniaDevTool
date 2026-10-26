using AvaloniaDevTool.Cli.Catalog;
using AvaloniaDevTool.Cli.Hosting.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AvaloniaDevTool.Cli.Hosting.Stdio;

public static class StdioMcpHost
{
    public static async Task<int> RunAsync(McpHostOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Transport != "stdio")
            throw new ArgumentException("The stdio host requires the stdio transport.", nameof(options));
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        await using var session = new ToolSession(options.PipeName);
        builder.Services.AddSingleton(session);
        var server = builder.Services.AddMcpServer().WithStdioServerTransport();
        McpToolCatalog.Register(server, session, options.Profile);
        await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
        return 0;
    }
}
