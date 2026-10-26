using AvaloniaDevTool.Cli.Hosting.Http;
using AvaloniaDevTool.Cli.Hosting.Options;
using AvaloniaDevTool.Cli.Hosting.Stdio;

namespace AvaloniaDevTool.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        McpHostOptions options;
        try
        {
            options = McpHostOptions.Parse(args);
        }
        catch (McpHostOptionsException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.WriteLine(McpHostOptions.HelpText);
            return 0;
        }

        try
        {
            return options.Transport == "http"
                ? await HttpMcpHost.RunAsync(options).ConfigureAwait(false)
                : await StdioMcpHost.RunAsync(options).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine($"host_start_failed: {error.Message}");
            return 1;
        }
    }
}
