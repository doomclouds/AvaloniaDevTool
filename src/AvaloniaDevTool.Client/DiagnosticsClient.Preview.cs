using System.Text.Json;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Client;

public sealed partial class DiagnosticsClient
{
    private long _contentGeneration;
    public long CurrentContentGeneration => Interlocked.Read(ref _contentGeneration);

    public async Task<long> RefreshContentGenerationAsync(CancellationToken cancellationToken = default)
    {
        long generation;
        try { generation = await InvokeAsync<long>(PreviewMethods.ContentGeneration, null, cancellationToken).ConfigureAwait(false); }
        catch (RpcException error) when (error.Code is "method_not_found" or "unknown_method")
        {
            // Older application bridges do not replace their document in a separate preview host.
            generation = 0;
        }
        return UpdateContentGeneration(generation);
    }

    private bool HandlePreviewNotification(string method, JsonElement payload)
    {
        if (method != PreviewMethods.ContentGenerationChanged) return false;
        UpdateContentGeneration(payload.GetInt64());
        return true;
    }

    private long UpdateContentGeneration(long generation)
    {
        var current = CurrentContentGeneration;
        while (generation > current)
        {
            var observed = Interlocked.CompareExchange(ref _contentGeneration, generation, current);
            if (observed == current) return generation;
            current = observed;
        }
        return current;
    }
}
