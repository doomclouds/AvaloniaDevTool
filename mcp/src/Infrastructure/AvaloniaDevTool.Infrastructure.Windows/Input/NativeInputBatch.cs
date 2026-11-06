using System.Runtime.InteropServices;
using AvaloniaDevTool.NativeAutomation;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal static class NativeInputBatch
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct OsInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    internal static uint InsertInputBatch(OsInput[] inputs, Func<OsInput[], uint> sender, uint moveCount = 0)
    {
        if (inputs.Length == 0)
            return 0;
        var inserted = 0u;
        var hasCount = false;
        try
        {
            inserted = sender(inputs);
            hasCount = true;
            if (inserted != inputs.Length)
                throw new NativeAutomationException("input_rejected",
                    $"Windows inserted {inserted} of {inputs.Length} input events after move count {moveCount}. Error={Marshal.GetLastWin32Error()}.");
            return inserted;
        }
        catch
        {
            // SendInput reports a count, not per-event acknowledgements. Interpret
            // serial insertion as a successful prefix for best-effort cleanup.
            // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput
            // If the sender throws without a count, release every possible down.
            var count = hasCount ? (int)Math.Min(inserted, (uint)inputs.Length) : inputs.Length;
            var releases = PendingInputReleases(inputs.AsSpan(0, count), hasCount);
            foreach (var release in releases)
            {
                // A rejected or throwing cleanup must not mask the original failure
                // or prevent an attempt to release the remaining keys/buttons.
                try
                {
                    sender([release]);
                }
                catch
                {
                }
            }

            throw;
        }
    }

    private static OsInput[] PendingInputReleases(ReadOnlySpan<OsInput> inputs, bool acknowledgeUps)
    {
        var held = new List<OsInput>();
        foreach (var input in inputs)
        {
            if (input.Type == 1)
            {
                var release = input;
                release.Data.Keyboard.Flags |= 0x0002;
                var index = held.FindIndex(candidate => candidate.Type == 1 &&
                                                        candidate.Data.Keyboard.VirtualKey ==
                                                        release.Data.Keyboard.VirtualKey &&
                                                        candidate.Data.Keyboard.ScanCode ==
                                                        release.Data.Keyboard.ScanCode &&
                                                        candidate.Data.Keyboard.Flags == release.Data.Keyboard.Flags);
                if ((input.Data.Keyboard.Flags & 0x0002) == 0)
                {
                    if (index < 0)
                        held.Add(release);
                }
                else if (acknowledgeUps && index >= 0)
                    held.RemoveAt(index);
            }
            else if (input.Type == 0)
            {
                TrackMouseButton(0x0002, 0x0004);
                TrackMouseButton(0x0008, 0x0010);

                void TrackMouseButton(uint down, uint up)
                {
                    var index = held.FindIndex(candidate => candidate.Type == 0 && candidate.Data.Mouse.Flags == up);
                    if ((input.Data.Mouse.Flags & down) != 0 && index < 0)
                    {
                        held.Add(new OsInput { Data = new InputUnion { Mouse = new MouseInput { Flags = up } } });
                        index = held.Count - 1;
                    }

                    if (acknowledgeUps && (input.Data.Mouse.Flags & up) != 0 && index >= 0)
                        held.RemoveAt(index);
                }
            }
        }

        held.Reverse();
        return held.ToArray();
    }
}
