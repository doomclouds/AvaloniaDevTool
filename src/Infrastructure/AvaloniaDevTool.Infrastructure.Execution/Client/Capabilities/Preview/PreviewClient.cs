using AvaloniaDevTool.Protocol;
using System.Text.Json;

namespace AvaloniaDevTool.Client;

public sealed class PreviewClient
{
    private readonly DiagnosticsConnection connection;
    private readonly Func<CancellationToken, Task<long>> readGeneration;

    internal PreviewClient(DiagnosticsConnection connection, Func<CancellationToken, Task<long>> readGeneration)
    {
        this.connection = connection;
        this.readGeneration = readGeneration;
        connection.NotificationReceived += HandlePreviewNotification;
        connection.SessionChanged += info =>
        {
            if (info is null)
                Interlocked.Exchange(ref _contentGeneration, 0);
            else
                UpdateContentGeneration(info.ContentGeneration);
        };
    }

    private long _contentGeneration;
    public long CurrentContentGeneration => Interlocked.Read(ref _contentGeneration);

    public async Task<long> RefreshContentGenerationAsync(CancellationToken cancellationToken = default)
    {
        var revision = connection.Revision;
        long generation;
        try
        {
            generation = await readGeneration(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!connection.TryApplyAtRevision(revision, static () => { }))
                throw StaleSession();
            throw;
        }

        var result = 0L;
        if (!connection.TryApplyAtRevision(revision, () => result = UpdateContentGeneration(generation)))
            throw StaleSession();
        return result;
    }

    private static RpcException StaleSession() =>
        new("stale_session", "The application connection changed while reading preview content generation.");

    private void HandlePreviewNotification(string method, JsonElement payload)
    {
        if (method != PreviewMethods.ContentGenerationChanged)
            return;
        UpdateContentGeneration(payload.GetInt64());
    }

    internal long UpdateContentGeneration(long generation)
    {
        var current = CurrentContentGeneration;
        while (generation > current)
        {
            var observed = Interlocked.CompareExchange(ref _contentGeneration, generation, current);
            if (observed == current)
                return generation;
            current = observed;
        }

        return current;
    }
}
