using ModelContextProtocol;

namespace AvaloniaDevTool.Cli;

/// <summary>Allocates managed node generations without reuse during one host lifetime.</summary>
public sealed class NodeGenerationAllocator
{
    public const int MaximumGeneration = 0xFFFFF;
    private int _lastGeneration;

    public long Allocate()
    {
        while (true)
        {
            var previous = Volatile.Read(ref _lastGeneration);
            if (previous >= MaximumGeneration)
                throw new McpException("session_limit: Restart the MCP server before attaching another application.");
            var next = previous + 1;
            if (Interlocked.CompareExchange(ref _lastGeneration, next, previous) == previous)
                return next;
        }
    }
}
