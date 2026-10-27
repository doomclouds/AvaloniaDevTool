using AvaloniaDevTool.UseCases;
using ModelContextProtocol;

namespace AvaloniaDevTool.Cli;

internal static class CapabilityErrors
{
    internal static async Task<T> AsMcpAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (CapabilityOperationException error)
        {
            throw new McpException(error.Message);
        }
    }
}
