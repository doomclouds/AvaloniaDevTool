using AvaloniaDevTool.NativeAutomation.Worker;
using Xunit;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed class NativeCursorArrivalTests
{
    [Fact]
    public void A_queued_move_may_leave_the_previous_position_visible_before_it_arrives()
    {
        var clock = new ManualClock();

        bool Read(out int x, out int y)
        {
            x = clock.Elapsed < TimeSpan.FromMilliseconds(20) ? 121 : 127;
            y = 128;
            return true;
        }

        var result = NativeCursorArrival.Wait(127, 128, Read, () => clock.Elapsed, clock.Pause);

        Assert.Equal(NativeCursorArrival.Outcome.Arrived, result.State);
        Assert.Equal((127, 128), (result.X, result.Y));
        Assert.True(result.Reads > 1);
        Assert.Equal(TimeSpan.FromMilliseconds(20), result.Elapsed);
        Assert.All(clock.Pauses, pause => Assert.InRange(pause, TimeSpan.FromTicks(1), TimeSpan.FromMilliseconds(5)));
    }

    [Fact]
    public void A_move_that_never_arrives_stops_polling_at_the_deadline_with_its_last_position()
    {
        var clock = new ManualClock();

        bool Read(out int x, out int y)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(100));
            x = 121;
            y = 128;
            return true;
        }

        var result = NativeCursorArrival.Wait(127, 128, Read, () => clock.Elapsed, clock.Pause);

        Assert.Equal(NativeCursorArrival.Outcome.TimedOut, result.State);
        Assert.Equal((121, 128), (result.X, result.Y));
        Assert.Equal(TimeSpan.FromMilliseconds(100), result.Elapsed);
    }

    [Fact]
    public void A_failed_cursor_read_cannot_confirm_arrival_even_if_its_out_values_match()
    {
        bool Read(out int x, out int y)
        {
            x = 127;
            y = 128;
            return false;
        }

        var result = NativeCursorArrival.Wait(127,
            128,
            Read,
            () => TimeSpan.Zero,
            _ => throw new InvalidOperationException("A failed read must not be retried."));

        Assert.Equal(NativeCursorArrival.Outcome.ReadFailed, result.State);
        Assert.Equal(1, result.Reads);
    }

    [Theory]
    [InlineData(1, -1, true)]
    [InlineData(-1, 1, true)]
    [InlineData(2, 0, false)]
    [InlineData(0, -2, false)]
    public void Arrival_keeps_the_original_one_pixel_tolerance(int dx, int dy, bool arrived)
    {
        var clock = new ManualClock();

        bool Read(out int x, out int y)
        {
            x = 127 + dx;
            y = 128 + dy;
            return true;
        }

        var result = NativeCursorArrival.Wait(127, 128, Read, () => clock.Elapsed, clock.Pause);

        Assert.Equal(arrived ? NativeCursorArrival.Outcome.Arrived : NativeCursorArrival.Outcome.TimedOut,
            result.State);
    }

    [Fact]
    public void A_poll_resuming_after_the_deadline_cannot_accept_a_late_arrival()
    {
        var elapsed = TimeSpan.Zero;
        var reads = 0;

        bool Read(out int x, out int y)
        {
            Assert.Equal(1, ++reads);
            x = 121;
            y = 128;
            return true;
        }

        var result = NativeCursorArrival.Wait(127,
            128,
            Read,
            () => elapsed,
            _ => elapsed = TimeSpan.FromMilliseconds(101));

        Assert.Equal(NativeCursorArrival.Outcome.TimedOut, result.State);
        Assert.Equal(1, result.Reads);
    }

    [Fact]
    public void A_cursor_reader_exception_is_preserved_without_more_polling()
    {
        var expected = new InvalidOperationException("Cursor reader failed.");
        bool Read(out int x, out int y) => throw expected;

        var actual = Assert.Throws<InvalidOperationException>(() => NativeCursorArrival.Wait(127,
            128,
            Read,
            () => TimeSpan.Zero,
            _ => throw new InvalidOperationException("No retry is permitted.")));

        Assert.Same(expected, actual);
    }

    [Fact]
    public void A_slow_cursor_read_cannot_confirm_a_point_after_the_deadline()
    {
        var elapsed = TimeSpan.Zero;

        bool Read(out int x, out int y)
        {
            elapsed = TimeSpan.FromMilliseconds(101);
            x = 127;
            y = 128;
            return true;
        }

        var result = NativeCursorArrival.Wait(127,
            128,
            Read,
            () => elapsed,
            _ => throw new InvalidOperationException("The deadline already passed."));

        Assert.Equal(NativeCursorArrival.Outcome.TimedOut, result.State);
        Assert.Equal(1, result.Reads);
    }

    private sealed class ManualClock
    {
        internal TimeSpan Elapsed { get; private set; }
        internal List<TimeSpan> Pauses { get; } = [];

        internal void Pause(TimeSpan duration)
        {
            Pauses.Add(duration);
            Elapsed += duration;
        }
    }
}
