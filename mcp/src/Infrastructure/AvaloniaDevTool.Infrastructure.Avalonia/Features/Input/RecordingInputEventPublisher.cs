using System.Threading.Channels;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Bounded observer transport; application input never waits for the diagnostic connection.</summary>
internal sealed class RecordingInputEventPublisher : IDisposable
{
    internal const int Capacity = 256;
    private readonly Func<string, object, CancellationToken, Task> _send;
    private readonly Channel<(Subscription Owner, RecordingInputEvent Event)> _queue =
        Channel.CreateBounded<(Subscription, RecordingInputEvent)>(new BoundedChannelOptions(Capacity)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
    private readonly CancellationTokenSource _stopping = new();
    private Subscription _subscription = new();
    private bool _disposed;

    private sealed class Subscription
    {
        internal readonly string Id = Guid.NewGuid().ToString("N");
        internal bool Enabled;
        internal long Sequence;
        internal long Published;
        internal long Dropped;
        internal long Pending;
    }

    internal RecordingInputEventPublisher(Func<string, object, CancellationToken, Task> send)
    {
        _send = send;
        _ = Task.Run(DeliverAsync);
    }

    internal bool Enabled => !_disposed && Volatile.Read(ref _subscription).Enabled;

    internal RecordingInputEventStatus Configure(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (enabled)
            Volatile.Write(ref _subscription, new Subscription { Enabled = true });
        else
            Volatile.Read(ref _subscription).Enabled = false;
        return Status();
    }

    internal RecordingInputEventStatus Status()
    {
        var owner = Volatile.Read(ref _subscription);
        return new(owner.Enabled && !_disposed, owner.Sequence == 0 && !owner.Enabled ? null : owner.Id,
            Interlocked.Read(ref owner.Published), Interlocked.Read(ref owner.Dropped),
            Interlocked.Read(ref owner.Pending));
    }

    internal void Dropped()
    {
        var owner = Volatile.Read(ref _subscription);
        if (owner.Enabled)
            Interlocked.Increment(ref owner.Dropped);
    }

    internal void Publish(Func<string, long, RecordingInputEvent> create)
    {
        var owner = Volatile.Read(ref _subscription);
        if (!owner.Enabled || _disposed)
            return;
        var observation = create(owner.Id, Interlocked.Increment(ref owner.Sequence));
        Interlocked.Increment(ref owner.Pending);
        if (!_queue.Writer.TryWrite((owner, observation)))
        {
            Interlocked.Decrement(ref owner.Pending);
            Interlocked.Increment(ref owner.Dropped);
        }
    }

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        var owner = Volatile.Read(ref _subscription);
        while (Interlocked.Read(ref owner.Pending) != 0)
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_stopping.Token).ConfigureAwait(false))
            {
                try
                {
                    if (!ReferenceEquals(item.Owner, Volatile.Read(ref _subscription)))
                    {
                        Interlocked.Increment(ref item.Owner.Dropped);
                        continue;
                    }
                    await _send(RecordingInputMethods.Event, item.Event, _stopping.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref item.Owner.Published);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or
                                                  OperationCanceledException or RpcException)
                {
                    Interlocked.Increment(ref item.Owner.Dropped);
                }
                finally
                {
                    Interlocked.Decrement(ref item.Owner.Pending);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Volatile.Read(ref _subscription).Enabled = false;
        _queue.Writer.TryComplete();
        _stopping.Cancel();
    }
}
