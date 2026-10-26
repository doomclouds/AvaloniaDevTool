using System.Text.Json;
using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Protocol;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class LoggingBudgetTests
{
    [AvaloniaFact]
    public async Task Unicode_and_escaped_log_history_fits_real_rpc_snapshot_and_keeps_session_usable()
    {
        var source = new DiagnosticsLogSource();
        var window = new Window { Content = new Button { Name = "BudgetTarget" } };
        window.Show();
        try
        {
            await using var agent = DiagnosticsAgent.Attach(Application.Current!,
                new DiagnosticsAgentOptions
                {
                    PipeName = "logging-budget-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window],
                    LogSources = [source]
                });
            await using var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            await client.StartActivityAsync(new ActivityStartRequest());
            var text = string.Concat(Enumerable.Repeat("中\"\\\n\u0001", 210));
            await Task.Run(() =>
            {
                for (var index = 0; index < 48; index++)
                    source.Publish(new("Warning",
                        "Budget",
                        "Entry {Index}",
                        [
                            new("Index", index),
                            .. Enumerable.Range(1, 31)
                                .Select(value => new KeyValuePair<string, object?>("P" + value, text))
                        ]));
            });
            var history = await client.GetActivityAsync();
            Assert.NotEmpty(history);
            Assert.Equal("47", history[^1].LogDetails!.Parameters[0].Value);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(history, ProtocolJson.Options).Length <
                        RpcPeer.MaximumFrameBytes - 4096);
            Assert.All(history,
                entry => Assert.True(JsonSerializer.SerializeToUtf8Bytes(entry, ProtocolJson.Options).Length <=
                                     ActivityService.MaximumEntryBytes));
            var status = await client.GetActivityStatusAsync();
            Assert.InRange(status.BufferedBytes, 1, status.ByteBudget);
            Assert.Equal(ActivityService.BufferedByteBudget, status.ByteBudget);
            Assert.True(status.ByteEvictedCount > 0);
            Assert.True(status.DroppedCount >= status.ByteEvictedCount);
            Assert.True(status.BudgetTruncatedCount > 0);
            Assert.All(history, entry => Assert.True(entry.LogDetails!.ByteBudgetTruncated));
            Assert.NotEmpty(await client.GetRootsAsync(TreeKind.Visual));
            await client.StopActivityAsync();
            await client.ClearActivityAsync();
            Assert.Empty(await client.GetActivityAsync());
            status = await client.GetActivityStatusAsync();
            Assert.Equal(0, status.BufferedBytes);
            Assert.Equal(0, status.ByteEvictedCount);
            Assert.Equal(0, status.BudgetTruncatedCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Maximum_scopes_and_exception_graph_share_one_encoded_text_budget_with_explicit_gaps()
    {
        var source = new DiagnosticsLogSource();
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { }, [source]);
        service.Start(new ActivityStartRequest());
        var text = string.Concat(Enumerable.Repeat("中\"\\\n\u0001", 210));
        var parameters = Enumerable.Range(0, 32).Select(index => new KeyValuePair<string, object?>("P" + index, text))
            .ToArray();
        var scopes = Enumerable.Range(0, 8).Select(_ => (IReadOnlyList<KeyValuePair<string, object?>>)parameters)
            .ToArray();
        source.Publish(new("Error",
            "Maximum",
            new string('中', 6000),
            parameters,
            new AggregateException(Enumerable.Range(0, 20).Select(_ => new Exception(text))),
            Scopes: scopes));
        var entry = Assert.Single(service.Snapshot());
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(entry, ProtocolJson.Options).Length <=
                    ActivityService.MaximumEntryBytes);
        Assert.True(entry.LogDetails!.ByteBudgetTruncated);
        Assert.Equal(32, entry.LogDetails.Parameters.Count);
        Assert.Contains(entry.LogDetails.Parameters, parameter => parameter.IsTruncated);
        Assert.Equal(8, entry.LogDetails.Scopes!.Count);
        Assert.All(entry.LogDetails.Scopes, scope => Assert.True(scope.IsTruncated));
        Assert.True(entry.LogDetails.Exception!.IsTruncated);
        Assert.NotEmpty(entry.LogDetails.Exception.InnerExceptions);
        Assert.Equal(1, service.Status().BudgetTruncatedCount);
        Assert.Equal(0, service.Status().DroppedCount);
    }

    [AvaloniaFact]
    public void Ordinary_32_scalar_parameters_remain_complete_under_the_entry_budget()
    {
        var source = new DiagnosticsLogSource();
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { }, [source]);
        service.Start(new ActivityStartRequest());
        source.Publish(new("Warning",
            "Ordinary",
            "Ordinary {P0}",
            Enumerable.Range(0, 32)
                .Select(index => new KeyValuePair<string, object?>("P" + index, new string('x', 1024))).ToArray()));
        var details = Assert.Single(service.Snapshot()).LogDetails!;
        Assert.False(details.ByteBudgetTruncated);
        Assert.False(details.ParametersTruncated);
        Assert.Equal(32, details.Parameters.Count);
        Assert.All(details.Parameters,
            parameter =>
            {
                Assert.Equal(1024, parameter.Value!.Length);
                Assert.False(parameter.IsTruncated);
            });
    }

    [AvaloniaFact]
    public async Task Concurrent_producers_and_clear_restart_keep_combined_byte_accounting_bounded()
    {
        var source = new DiagnosticsLogSource();
        var notified = 0;
        var oversizedNotification = false;
        using var service = new ActivityService(new WeakObjectRegistry(),
            (method, payload) =>
            {
                if (method != ActivityMethods.EntryAdded)
                    return;
                oversizedNotification |= JsonSerializer.SerializeToUtf8Bytes(payload, ProtocolJson.Options).Length >
                                         ActivityService.MaximumEntryBytes;
                notified++;
            },
            [source]);
        service.Start(new ActivityStartRequest());
        var text = string.Concat(Enumerable.Repeat("中\"\\\n\u0001", 210));
        var parameters = Enumerable.Range(0, 32).Select(index => new KeyValuePair<string, object?>("P" + index, text))
            .ToArray();
        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            for (var index = 0; index < 120; index++)
                source.Publish(new("Warning", "Concurrent", $"Worker {worker}: {index}", parameters));
        })).ToArray();
        for (var cycle = 0; cycle < 6; cycle++)
        {
            await Task.Delay(3);
            _ = service.Snapshot();
            var status = service.Status();
            Assert.InRange(status.BufferedBytes, 0, ActivityService.BufferedByteBudget);
            service.Clear();
            if (cycle % 2 == 0)
            {
                service.Stop();
                service.Start(new ActivityStartRequest());
            }
        }

        await Task.WhenAll(workers);
        service.Stop();
        Assert.InRange(service.Status().BufferedBytes, 0, ActivityService.BufferedByteBudget);
        Assert.True(notified > 0);
        Assert.False(oversizedNotification);
        service.Clear();
        Assert.Equal(0, service.Status().BufferedBytes);
        Assert.Equal(0, service.Status().DroppedCount);
        service.Start(new ActivityStartRequest());
        source.Publish(new("Warning", "Recovered", "Small entry", []));
        Assert.Equal("Small entry", Assert.Single(service.Snapshot()).Message);
    }

    [AvaloniaFact]
    public async Task Slow_notification_sender_stays_bounded_while_snapshots_remain_readable_and_recovers_in_order()
    {
        var source = new DiagnosticsLogSource();
        var sender = new ControlledSender();
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { }, [source], sender.SendAsync);
        service.Start(new ActivityStartRequest());
        var text = new string('中', 1024);
        for (var index = 0; index < 300; index++)
            source.Publish(new("Warning", "Slow", "Entry {Index}: {Value}", [new("Index", index), new("Value", text)]));
        _ = service.Snapshot();
        await WaitUntilAsync(() => sender.EntryStarted > 0);
        for (var index = 300; index < 600; index++)
            source.Publish(new("Warning", "Slow", "Entry {Index}: {Value}", [new("Index", index), new("Value", text)]));
        var history = service.Snapshot();
        Assert.Equal("599", history[^1].LogDetails!.Parameters[0].Value);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(history, ProtocolJson.Options).Length <
                    RpcPeer.MaximumFrameBytes - 4096);
        var status = service.Status();
        Assert.InRange(status.BufferedBytes, 1, ActivityService.BufferedByteBudget);
        Assert.InRange(status.NotificationBufferedBytes, 1, ActivityService.NotificationByteBudget);
        Assert.InRange(status.PendingNotificationCount, 1, ActivityService.NotificationCapacity);
        Assert.True(status.NotificationDroppedCount > 0);
        Assert.Equal(1, sender.MaximumActive);
        sender.Release();
        await WaitUntilAsync(() => service.Status().PendingNotificationCount == 0);
        var sequences = sender.Completed.ToArray();
        Assert.Equal(sequences.OrderBy(value => value), sequences);
        Assert.Equal(sequences.Distinct().Count(), sequences.Length);
        Assert.Contains(history[^1].Sequence, sequences);

        sender.Block();
        var started = sender.EntryStarted;
        source.Publish(new("Warning", "Slow", "Clear cancellation", []));
        _ = service.Snapshot();
        await WaitUntilAsync(() => sender.EntryStarted > started);
        service.Clear();
        await WaitUntilAsync(() => sender.Active == 0 && service.Status().PendingNotificationCount == 0);
        Assert.Empty(service.Snapshot());
        Assert.Equal(0, service.Status().NotificationBufferedBytes);
        Assert.Equal(0, service.Status().NotificationDroppedCount);

        sender.Block();
        started = sender.EntryStarted;
        source.Publish(new("Warning", "Slow", "Stop cancellation", []));
        _ = service.Snapshot();
        await WaitUntilAsync(() => sender.EntryStarted > started);
        service.Stop();
        await WaitUntilAsync(() => sender.Active == 0 && service.Status().PendingNotificationCount == 0);
        Assert.False(source.HasSubscribers);
        Assert.NotEmpty(service.Snapshot());
        Assert.Equal(0, service.Status().NotificationBufferedBytes);

        service.Start(new ActivityStartRequest());
        sender.Block();
        started = sender.EntryStarted;
        source.Publish(new("Warning", "Slow", "Dispose cancellation", []));
        _ = service.Snapshot();
        await WaitUntilAsync(() => sender.EntryStarted > started);
        service.Dispose();
        await WaitUntilAsync(() => sender.Active == 0);
        Assert.False(source.HasSubscribers);
        Assert.Equal(1, sender.MaximumActive);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < end)
            await Task.Delay(5);
        Assert.True(predicate());
    }

    private sealed class ControlledSender
    {
        private TaskCompletionSource _gate = NewGate();
        private int _active;
        private int _maximumActive;
        private int _entryStarted;
        public ConcurrentQueue<long> Completed { get; } = new();
        public int Active => Volatile.Read(ref _active);
        public int MaximumActive => Volatile.Read(ref _maximumActive);
        public int EntryStarted => Volatile.Read(ref _entryStarted);
        public void Block() => Volatile.Write(ref _gate, NewGate());
        public void Release() => Volatile.Read(ref _gate).TrySetResult();

        public async Task SendAsync(string method, object payload, CancellationToken token)
        {
            var active = Interlocked.Increment(ref _active);
            Interlocked.Exchange(ref _maximumActive, Math.Max(active, MaximumActive));
            try
            {
                if (method != ActivityMethods.EntryAdded)
                    return;
                Interlocked.Increment(ref _entryStarted);
                await Volatile.Read(ref _gate).Task.WaitAsync(token);
                token.ThrowIfCancellationRequested();
                Completed.Enqueue(((ActivityEntry)payload).Sequence);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
