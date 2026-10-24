using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace AvaloniaDevTool.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            Console.WriteLine("AvaloniaDevTool.Cli mcp [--pipe NAME]");
            Console.WriteLine("Starts the independent stdio MCP server. Use attach-to-app to choose a local target.");
            return 0;
        }
        if (args[0] != "mcp" || (args.Length != 1 && (args.Length != 3 || args[1] != "--pipe")))
        {
            Console.Error.WriteLine("Usage: AvaloniaDevTool.Cli mcp [--pipe NAME]");
            return 2;
        }
        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(_ => new ToolSession(args.Length == 3 ? args[2] : null));
        builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<InspectorTools>();
        await builder.Build().RunAsync();
        return 0;
    }
}

