using System.Text.Json;
using AvaloniaDevTool.Cli.Sessions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace AvaloniaDevTool.Cli.Tests;

public sealed class HttpSessionLifetimeTests
{
    [Fact]
    public void Desktop_lease_rejects_competitors_and_an_old_handle_cannot_release_a_new_owner()
    {
        var desktop = new DesktopControlLease();
        var firstOwner = new object();
        var secondOwner = new object();
        var first = desktop.Acquire(firstOwner);
        Assert.Same(first, desktop.Acquire(firstOwner));
        Assert.Contains("controller_busy", Assert.Throws<McpException>(() => desktop.Acquire(secondOwner)).Message);

        first.Dispose();
        using var second = desktop.Acquire(secondOwner);
        first.Dispose();
        Assert.Contains("controller_busy", Assert.Throws<McpException>(() => desktop.Acquire(firstOwner)).Message);
    }

    [Fact]
    public async Task Application_discovery_and_detach_do_not_claim_another_sessions_desktop()
    {
        var desktop = new DesktopControlLease();
        using var existing = desktop.Acquire(new object());
        await using var session = CreateSession(desktop);

        await session.ExecuteToolAsync(Call("attach-to-app"), Success, CancellationToken.None);
        await session.ExecuteToolAsync(Call("attach-to-app", 0), Success, CancellationToken.None);
        await session.ExecuteToolAsync(Call("detach"), Success, CancellationToken.None);
        var busy = await Assert.ThrowsAsync<McpException>(() =>
            session.ExecuteToolAsync(Call("attach-to-app", 1), Success, CancellationToken.None));
        Assert.Contains("controller_busy", busy.Message);
    }

    [Fact]
    public async Task Failed_or_cancelled_first_target_call_releases_its_provisional_lease_without_hiding_the_error()
    {
        var desktop = new DesktopControlLease();
        await using var first = CreateSession(desktop);
        await using var second = CreateSession(desktop);
        var expected = new McpException("invalid_argument: Expected schema or business failure.");

        var failure = await Assert.ThrowsAsync<McpException>(() => first.ExecuteToolAsync(Call("attach-to-file"),
            _ => Task.FromException<CallToolResult>(expected),
            CancellationToken.None));
        Assert.Same(expected, failure);
        await second.ExecuteToolAsync(Call("ui-click"), Success, CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.ExecuteToolAsync(Call("ui-click"),
            token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<CallToolResult>(token);
            },
            cancellation.Token));
        await second.ExecuteToolAsync(Call("ui-click"), Success, CancellationToken.None);
    }

    [Fact]
    public async Task A_failed_call_does_not_release_the_lease_borrowed_by_another_active_call_in_the_same_session()
    {
        var desktop = new DesktopControlLease();
        await using var first = CreateSession(desktop);
        await using var competitor = CreateSession(desktop);
        var releaseFirst = NewCompletion();
        var releaseSecond = NewCompletion();
        var firstCall = first.ExecuteToolAsync(Call("ui-click"),
            async _ =>
            {
                await releaseFirst.Task;
                throw new McpException("expected_failure: First provisional call failed.");
            },
            CancellationToken.None);
        var secondCall = first.ExecuteToolAsync(Call("ui-click"),
            async _ =>
            {
                await releaseSecond.Task;
                return await Success(CancellationToken.None);
            },
            CancellationToken.None);
        try
        {
            releaseFirst.SetResult();
            await Assert.ThrowsAsync<McpException>(() => firstCall);
            Assert.Contains("controller_busy",
                (await Assert.ThrowsAsync<McpException>(() =>
                    competitor.ExecuteToolAsync(Call("ui-click"), Success, CancellationToken.None))).Message);
        }
        finally
        {
            releaseSecond.TrySetResult();
        }

        await secondCall;
        await competitor.ExecuteToolAsync(Call("ui-click"), Success, CancellationToken.None);
    }

    [Fact]
    public async Task Idle_expiration_ignores_active_calls_and_starts_a_fresh_idle_window_after_completion()
    {
        var clock = new ManualClock();
        await using var session = CreateSession(new DesktopControlLease(), clock);
        var release = NewCompletion();
        var active = session.ExecuteToolAsync(Call("ui-click"),
            async _ =>
            {
                await release.Task;
                return await Success(CancellationToken.None);
            },
            CancellationToken.None);
        try
        {
            clock.Advance(TimeSpan.FromMinutes(20));
            Assert.False(session.TryExpire());
            Assert.False(session.Expired.IsCancellationRequested);
        }
        finally
        {
            release.SetResult();
        }

        await active;

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(session.TryExpire());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(session.TryExpire());
        Assert.True(session.Expired.IsCancellationRequested);
        Assert.Contains("session_expired",
            (await Assert.ThrowsAsync<McpException>(() =>
                session.ExecuteToolAsync(Call("attach-to-app"), Success, CancellationToken.None))).Message);
    }

    [Fact]
    public async Task A_protocol_message_renews_idle_time_and_a_host_stop_does_not_masquerade_as_idle_expiration()
    {
        var clock = new ManualClock();
        await using var session = CreateSession(new DesktopControlLease(), clock);
        clock.Advance(TimeSpan.FromMinutes(9));
        session.Touch();
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(session.TryExpire());

        using var stop = new CancellationTokenSource();
        var monitor = session.MonitorIdleAsync(stop.Token);
        stop.Cancel();
        await monitor;
        Assert.False(session.Expired.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(session.TryExpire());
    }

    [Fact]
    public async Task Closing_waits_for_active_calls_and_keeps_control_until_cleanup_finishes()
    {
        var desktop = new DesktopControlLease();
        await using var first = CreateSession(desktop);
        await using var competitor = CreateSession(desktop);
        var release = NewCompletion();
        var call = first.ExecuteToolAsync(Call("ui-click"),
            async _ =>
            {
                await release.Task;
                return await Success(CancellationToken.None);
            },
            CancellationToken.None);
        var disposal = first.DisposeAsync().AsTask();
        try
        {
            Assert.True(first.Expired.IsCancellationRequested);
            Assert.False(disposal.IsCompleted);
            Assert.Contains("session_expired",
                (await Assert.ThrowsAsync<McpException>(() =>
                    first.ExecuteToolAsync(Call("attach-to-app"), Success, CancellationToken.None))).Message);
            Assert.Contains("controller_busy",
                (await Assert.ThrowsAsync<McpException>(() =>
                    competitor.ExecuteToolAsync(Call("ui-click"), Success, CancellationToken.None))).Message);
        }
        finally
        {
            release.TrySetResult();
        }

        await call;
        await disposal;
        await first.DisposeAsync();
        await competitor.ExecuteToolAsync(Call("ui-click"), Success, CancellationToken.None);
    }

    private static HttpToolSession CreateSession(DesktopControlLease desktop, TimeProvider? time = null) =>
        new(null, new NodeGenerationAllocator(), desktop, time);

    private static CallToolRequestParams Call(string name, long? applicationId = null) => new()
    {
        Name = name,
        Arguments = applicationId is { } id
            ? new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement(id) }
            : null
    };

    private static Task<CallToolResult> Success(CancellationToken _) =>
        Task.FromResult(new CallToolResult { Content = [] });

    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _ticks);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }
}
