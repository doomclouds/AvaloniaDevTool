using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AvaloniaDevTool.NativeAutomation.Fixture;

internal static class NativeHwndReuseFixture
{
    private const uint CommandMessage = 0x8001;
    private const uint QuitMessage = 0x0012;
    private const uint NoActivateToolWindow = 0x08000080;
    private const uint PopupWindow = 0x80000000;
    private const uint VisibleChildEdit = 0x50000080;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static bool TryRun(string[] arguments)
    {
        if (arguments.FirstOrDefault() != "--native-hwnd-reuse") return false;
        if (arguments.Length != 1) throw new ArgumentException("The HWND fixture takes no additional arguments.");
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        using var fixture = new WindowCycle();
        fixture.Run();
        return true;
    }

    private sealed class WindowCycle : IDisposable
    {
        private const int MaximumBlockers = 32;
        private const long MaximumWorkingSetBytes = 256L * 1024 * 1024;
        private const int MaximumKernelHandles = 1024;
        private const uint MaximumUserObjects = 128;
        private readonly ConcurrentQueue<JsonElement> _commands = new();
        private readonly List<nint> _blockers = [];
        private readonly uint _threadId = GetCurrentThreadId();
        private readonly Process _process = Process.GetCurrentProcess();
        private nint _root;
        private nint _edit;
        private long _originalRoot;
        private long _originalEdit;
        private int _generation;
        private int _createdWindows;
        private int _originalSlotVisits;
        private int _blockersRetained;
        private int _peakOwnedWindows;
        private string? _budgetReason;

        internal void Run()
        {
            // Establish the UI thread's queue before the stdin reader can post commands.
            PeekMessage(out _, 0, 0, 0, 0);
            CreatePair();
            _originalRoot = _root.ToInt64();
            _originalEdit = _edit.ToInt64();
            ShowWindow(_root, 4);
            WriteState("ready");
            _ = Task.Run(ReadCommands);
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (message.Id == CommandMessage)
                {
                    while (_commands.TryDequeue(out var command)) Execute(command);
                }
                else
                {
                    TranslateMessage(in message);
                    DispatchMessage(in message);
                }
            }
        }

        private void ReadCommands()
        {
            try
            {
                while (Console.ReadLine() is { } line)
                {
                    using var document = JsonDocument.Parse(line);
                    _commands.Enqueue(document.RootElement.Clone());
                    if (!PostThreadMessage(_threadId, CommandMessage, 0, 0))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally { PostThreadMessage(_threadId, QuitMessage, 0, 0); }
        }

        private void Execute(JsonElement command)
        {
            switch (command.GetProperty("command").GetString())
            {
                case "destroy":
                    DestroyPair();
                    WriteState("destroyed");
                    break;
                case "recreate":
                    if (_root != 0) throw new InvalidOperationException("Destroy the current window before recreating it.");
                    CreatePair();
                    ShowWindow(_root, 4);
                    WriteState("recreated");
                    break;
                case "reuse":
                    SeekReuse(command.GetProperty("maxIterations").GetInt32(), command.GetProperty("budgetMs").GetInt32());
                    break;
                case "state": WriteState("state"); break;
                case "exit": PostQuitMessage(0); break;
                default: throw new InvalidOperationException("Unknown HWND fixture command.");
            }
        }

        private void SeekReuse(int maxIterations, int budgetMs)
        {
            if (maxIterations is < 1 or > 2_000_000 || budgetMs is < 1 or > 180_000)
                throw new ArgumentOutOfRangeException(nameof(maxIterations), "The HWND probe is limited to 2,000,000 iterations and 180 seconds.");
            if (_root != 0) throw new InvalidOperationException("Destroy the original window before probing for reuse.");
            var timer = Stopwatch.StartNew();
            var iterations = 0;
            var indices = new HashSet<int>();
            var reused = false;
            var originalSlot = unchecked((int)_originalRoot) & 0xffff;
            try
            {
                while (iterations < maxIterations && timer.ElapsedMilliseconds < budgetMs)
                {
                    CreateRoot();
                    iterations++;
                    // Slot statistics guide allocation only; success compares the entire original HWND.
                    var slot = unchecked((int)_root.ToInt64()) & 0xffff;
                    indices.Add(slot);
                    if (slot == originalSlot)
                    {
                        _originalSlotVisits++;
                        reused = _root.ToInt64() == _originalRoot;
                        if (reused) break;
                    }
                    else
                    {
                        if (_blockers.Count == MaximumBlockers) { _budgetReason = "blocker_limit"; break; }
                        AssertOwned(_root);
                        _blockers.Add(_root);
                        _blockersRetained++;
                        _root = 0;
                        if (_blockers.Count == MaximumBlockers && _originalSlotVisits == 0)
                        { _budgetReason = "original_slot_unreached"; break; }
                    }
                    if ((iterations & 255) == 0 && ResourceLimit() is { } resourceLimit)
                    { _budgetReason = resourceLimit; break; }
                    if (_root != 0 && iterations < maxIterations && timer.ElapsedMilliseconds < budgetMs) DestroyPair();
                }
                if (!reused) _budgetReason ??= timer.ElapsedMilliseconds >= budgetMs ? "time_limit" : "iteration_limit";
            }
            finally { DestroyBlockers(); }
            // Retain a real replacement even if the budget expired between iterations.
            if (_root == 0) CreateRoot();
            CreateEdit();
            ShowWindow(_root, 4);
            WriteState(reused ? "reused" : "budget_exhausted", iterations, timer.ElapsedMilliseconds, indices.Count);
        }

        private void CreatePair()
        {
            CreateRoot();
            CreateEdit();
        }

        private void CreateRoot()
        {
            _generation++;
            _root = CreateWindowEx(NoActivateToolWindow, "STATIC", "AVDT HWND Cycle " + _generation.ToString(CultureInfo.InvariantCulture),
                PopupWindow, 32, 32, 240, 100, 0, 0, 0, 0);
            if (_root == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _createdWindows++;
            AssertOwned(_root);
            RecordOwnedWindowCount();
        }

        private void CreateEdit()
        {
            _edit = CreateWindowEx(0, "EDIT", "generation:" + _generation.ToString(CultureInfo.InvariantCulture),
                VisibleChildEdit, 8, 8, 200, 24, _root, 202, 0, 0);
            if (_edit == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _createdWindows++;
            AssertOwned(_edit);
            RecordOwnedWindowCount();
        }

        private void RecordOwnedWindowCount()
        {
            var count = _blockers.Count + (_root == 0 ? 0 : 1) + (_edit == 0 ? 0 : 1);
            _peakOwnedWindows = Math.Max(_peakOwnedWindows, count);
            if (count > MaximumBlockers + 2) throw new InvalidOperationException("The fixture exceeded its 34-window limit.");
        }

        private string? ResourceLimit()
        {
            _process.Refresh();
            if (_process.WorkingSet64 > MaximumWorkingSetBytes) return "working_set_limit";
            if (_process.HandleCount > MaximumKernelHandles) return "kernel_handle_limit";
            if (GetGuiResources(_process.Handle, 1) > MaximumUserObjects) return "user_object_limit";
            return null;
        }

        private void DestroyBlockers()
        {
            for (var index = _blockers.Count - 1; index >= 0; index--)
            {
                var window = _blockers[index];
                AssertOwned(window);
                if (!DestroyWindow(window)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (IsWindow(window)) throw new InvalidOperationException("A fixture blocker was not destroyed.");
                _blockers.RemoveAt(index);
            }
        }

        private void DestroyPair()
        {
            if (_root == 0) return;
            AssertOwned(_root);
            if (_edit != 0) AssertOwned(_edit);
            if (!DestroyWindow(_root)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (IsWindow(_root) || IsWindow(_edit)) throw new InvalidOperationException("The fixture window was not destroyed.");
            _root = 0;
            _edit = 0;
        }

        private void AssertOwned(nint handle)
        {
            var threadId = GetWindowThreadProcessId(handle, out var processId);
            if (processId != _process.Id || threadId != _threadId)
                throw new InvalidOperationException("Only this fixture thread's own windows may be used.");
        }

        private void WriteState(string status, int iterations = 0, long elapsedMs = 0, int distinctIndices = 0)
        {
            var value = new StringBuilder(256);
            if (_edit != 0) GetWindowText(_edit, value, value.Capacity);
            _process.Refresh();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                status, processId = _process.Id, startTimeUtc = _process.StartTime.ToUniversalTime(),
                originalRoot = _originalRoot, originalEdit = _originalEdit,
                currentRoot = _root.ToInt64(), currentEdit = _edit.ToInt64(),
                rootReused = _generation > 1 && _root != 0 && _root.ToInt64() == _originalRoot,
                editReused = _generation > 1 && _edit != 0 && _edit.ToInt64() == _originalEdit,
                generation = _generation, editValue = value.ToString(), createdWindows = _createdWindows,
                iterations, elapsedMs, distinctIndices, originalSlotVisits = _originalSlotVisits,
                blockersRetained = _blockersRetained, blockersRemaining = _blockers.Count,
                peakOwnedWindows = _peakOwnedWindows, workingSetBytes = _process.WorkingSet64,
                kernelHandles = _process.HandleCount, userObjects = GetGuiResources(_process.Handle, 1), budgetReason = _budgetReason
            }, JsonOptions));
            Console.Out.Flush();
        }

        public void Dispose()
        {
            try { DestroyPair(); }
            finally
            {
                try { DestroyBlockers(); }
                finally { _process.Dispose(); }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, StringBuilder text, int maxCount);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(in Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(in Message message);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
}
