using System.Runtime.InteropServices;
using Xunit;
using static AvaloniaDevTool.NativeAutomation.Worker.NativeAutomationBackend;

namespace AvaloniaDevTool.NativeAutomation.Tests;

public sealed class NativeInputBatchTests
{
    [Fact]
    public void Partial_sender_counts_release_only_the_keys_and_buttons_left_down_in_each_prefix()
    {
        // These are sender simulations, not reproductions of partial OS insertion.
        var cases = new (OsInput[] Batch, OsInput[][] ReleasesByPrefix)[]
        {
            ([Key(0x11), Key(0x10), Mouse(2), Mouse(4), Key(0x10, true), Key(0x11, true)],
                [[], [Key(0x11, true)], [Key(0x10, true), Key(0x11, true)],
                    [Mouse(4), Key(0x10, true), Key(0x11, true)], [Key(0x10, true), Key(0x11, true)], [Key(0x11, true)]]),
            ([Key(0x11), Mouse(8), Mouse(16), Key(0x11, true)],
                [[], [Key(0x11, true)], [Mouse(16), Key(0x11, true)], [Key(0x11, true)]]),
            ([Mouse(2), Mouse(4), Mouse(2), Mouse(4)],
                [[], [Mouse(4)], [], [Mouse(4)]]),
            ([Mouse(2), Mouse(0xC001), Mouse(0xC001), Mouse(4)],
                [[], [Mouse(4)], [Mouse(4)], [Mouse(4)]]),
            ([Key(0x11), Key(0x25, extended: true), Key(0x25, true, true), Key(0x11, true)],
                [[], [Key(0x11, true)], [Key(0x25, true, true), Key(0x11, true)], [Key(0x11, true)]]),
            ([Unicode('A'), Unicode('A', true), Unicode('\u4E2D'), Unicode('\u4E2D', true), Unicode('A'), Unicode('A', true)],
                [[], [Unicode('A', true)], [], [Unicode('\u4E2D', true)], [], [Unicode('A', true)]]),
            ([Key(0x11), Key(0x11), Key(0x11, true), Key(0x11, true)],
                [[], [Key(0x11, true)], [Key(0x11, true)], []])
        };

        foreach (var (batch, releasesByPrefix) in cases)
        {
            Assert.Equal(batch.Length, releasesByPrefix.Length);
            var original = batch.Select(Describe).ToArray();
            for (var prefix = 0; prefix < batch.Length; prefix++)
            {
                var calls = new List<OsInput[]>();
                var error = Assert.Throws<NativeAutomationException>(() => InsertInputBatch(batch, inputs =>
                {
                    calls.Add(inputs);
                    return calls.Count == 1 ? (uint)prefix : (uint)inputs.Length;
                }));
                Assert.Equal("input_rejected", error.Code);
                Assert.Same(batch, calls[0]);
                Assert.All(calls.Skip(1), release => Assert.Single(release));
                Assert.Equal(releasesByPrefix[prefix].Select(Describe), calls.Skip(1).SelectMany(inputs => inputs).Select(Describe));
                Assert.Equal(original, batch.Select(Describe));
            }
        }
    }

    [Fact]
    public void Cleanup_rejection_and_exception_preserve_the_original_error_and_attempt_every_release()
    {
        OsInput[] batch = [Key(0x11), Key('A'), Mouse(2), Mouse(4), Key('A', true), Key(0x11, true)];
        var calls = new List<OsInput[]>();
        var error = Assert.Throws<NativeAutomationException>(() => InsertInputBatch(batch, inputs =>
        {
            calls.Add(inputs);
            if (calls.Count == 1)
            {
                Marshal.SetLastPInvokeError(5);
                return 3;
            }
            Marshal.SetLastPInvokeError(999);
            if (calls.Count == 2) return 0;
            if (calls.Count == 3) throw new InvalidOperationException("Cleanup sender failed.");
            return (uint)inputs.Length;
        }, moveCount: 1));

        Assert.Equal("input_rejected", error.Code);
        Assert.Contains("Windows inserted 3 of 6 input events after move count 1. Error=5.", error.Message);
        Assert.Equal(new[] { Mouse(4), Key('A', true), Key(0x11, true) }.Select(Describe),
            calls.Skip(1).SelectMany(inputs => inputs).Select(Describe));
    }

    [Fact]
    public void Throwing_sender_preserves_its_exception_and_attempts_all_possible_releases_without_an_acknowledged_count()
    {
        OsInput[] batch = [Key(0x11), Key('A'), Mouse(2), Mouse(4), Key('A', true), Key(0x11, true)];
        var original = new InvalidOperationException("Insertion sender failed.");
        var calls = new List<OsInput[]>();
        var error = Assert.Throws<InvalidOperationException>(() => InsertInputBatch(batch, inputs =>
        {
            calls.Add(inputs);
            if (calls.Count == 1) throw original;
            throw new InvalidOperationException("Cleanup sender failed.");
        }));

        Assert.Same(original, error);
        Assert.Equal(new[] { Mouse(4), Key('A', true), Key(0x11, true) }.Select(Describe),
            calls.Skip(1).SelectMany(inputs => inputs).Select(Describe));
    }

    [Fact]
    public void Successful_sender_receives_the_original_batch_once_and_returns_its_original_count()
    {
        OsInput[][] batches =
        [
            [Mouse(2), Mouse(4)],
            [Mouse(2), Mouse(4), Mouse(2), Mouse(4)],
            [Key(0x11), Key('A'), Key('A', true), Key(0x11, true)],
            [Unicode('A'), Unicode('A', true)]
        ];
        foreach (var batch in batches)
        {
            var original = batch.Select(Describe).ToArray();
            var calls = 0;
            var inserted = InsertInputBatch(batch, inputs =>
            {
                calls++;
                Assert.Same(batch, inputs);
                return (uint)inputs.Length;
            }, moveCount: 1);
            Assert.Equal((uint)batch.Length, inserted);
            Assert.Equal(1, calls);
            Assert.Equal(original, batch.Select(Describe));
        }
        Assert.Equal(0u, InsertInputBatch([], _ => throw new InvalidOperationException("An empty batch must not reach the sender.")));
    }

    private static OsInput Key(ushort key, bool up = false, bool extended = false) => new()
    {
        Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput
        { VirtualKey = key, Flags = (up ? 2u : 0) | (extended ? 1u : 0) } }
    };

    private static OsInput Unicode(char character, bool up = false) => new()
    {
        Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = up ? 6u : 4u } }
    };

    private static OsInput Mouse(uint flags) => new()
    { Data = new InputUnion { Mouse = new MouseInput { Flags = flags } } };

    private static string Describe(OsInput input) => input.Type == 1
        ? $"key:{input.Data.Keyboard.VirtualKey}:{input.Data.Keyboard.ScanCode}:{input.Data.Keyboard.Flags}"
        : $"mouse:{input.Data.Mouse.Flags}";
}
