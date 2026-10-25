using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using AvaloniaDevTool.NativeAutomation;

namespace AvaloniaDevTool.NativeAutomation.Worker;

/// <summary>Invalidates tracked HWND lifetimes when a destruction event is delivered.</summary>
/// <remarks>
/// WinEvent delivery is asynchronous. A current stamp proves that no destruction has been
/// observed, not that Windows has delivered every event. In particular, Capture is not a
/// synchronization barrier. Capture before acquiring the root UIA object and Validate again
/// before publishing its ID. A delayed event may conservatively invalidate that new ID.
/// https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwineventhook
/// https://learn.microsoft.com/windows/win32/winauto/out-of-context-hook-functions
/// </remarks>
internal sealed class NativeWindowLifetimeMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<nint, Stamp> _windows = [];
    private readonly INativeWindowLifetimeEventSource _source;
    private Exception? _failure;
    private bool _disposed;

    internal NativeWindowLifetimeMonitor(INativeWindowLifetimeEventSource? source = null)
    {
        _source = source ?? new WinEventSource();
        try { _source.Start(Destroyed, Failed); }
        catch (Exception exception)
        {
            Failed(exception);
            _source.Dispose();
            throw Unavailable(exception);
        }
    }

    internal NativeWindowLifetimeMonitor(int processId) : this()
    {
        try { EnsureProcess(processId); }
        catch { Dispose(); throw; }
    }

    internal Stamp Capture(nint handle, int processId)
    {
        if (handle == 0 || processId <= 0) throw new ArgumentOutOfRangeException(nameof(handle));
        EnsureProcess(processId);
        lock (_gate)
        {
            EnsureHealthy();
            if (!_windows.TryGetValue(handle, out var stamp))
                _windows[handle] = stamp = new Stamp(handle, processId, 0);
            else if (stamp.ProcessId != processId)
                _windows[handle] = stamp = stamp with { ProcessId = processId, Epoch = checked(stamp.Epoch + 1) };
            return stamp;
        }
    }

    private void EnsureProcess(int processId)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        lock (_gate) EnsureHealthy();
        // Each owned window process gets a hook on the same message-pump thread.
        // Acknowledgement confirms installation only; it does not drain WinEvents.
        try { _source.EnsureProcess(processId); }
        catch (Exception exception)
        {
            Failed(exception);
            throw Unavailable(exception);
        }
    }

    internal void Validate(Stamp stamp)
    {
        lock (_gate)
        {
            EnsureHealthy();
            if (!_windows.TryGetValue(stamp.Handle, out var current) || current != stamp)
                throw new NativeAutomationException("stale_node", "The native window lifetime ended. Enumerate the native tree again.");
        }
    }

    private void Destroyed(nint handle)
    {
        lock (_gate)
        {
            if (_disposed || !_windows.TryGetValue(handle, out var stamp)) return;
            // Never reset or remove epochs: an old stamp must not become current again.
            if (stamp.Epoch == long.MaxValue)
                _failure ??= new InvalidOperationException("The native window lifetime counter was exhausted.");
            else
                _windows[handle] = stamp with { Epoch = stamp.Epoch + 1 };
        }
    }

    private void Failed(Exception exception)
    {
        lock (_gate) _failure ??= exception;
    }

    private void EnsureHealthy()
    {
        if (_disposed) throw Unavailable(new ObjectDisposedException(nameof(NativeWindowLifetimeMonitor)));
        if (_failure is not null) throw Unavailable(_failure);
    }

    private static NativeAutomationException Unavailable(Exception exception) => new("lifetime_monitor_unavailable",
        "Native window lifetime monitoring failed. Attach again and obtain new node IDs.", exception);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _source.Dispose();
    }

    internal readonly record struct Stamp(nint Handle, int ProcessId, long Epoch);

    private sealed class WinEventSource : INativeWindowLifetimeEventSource
    {
        private const uint DestroyEvent = 0x8001;
        private const uint InstallMessage = 0x8001;
        private const uint QuitMessage = 0x0012;
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);
        private readonly ConcurrentQueue<HookRequest> _requests = new();
        private readonly ConcurrentDictionary<int, nint> _hooks = new();
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly WinEventCallback _callback;
        private Thread? _thread;
        private Action<nint>? _destroyed;
        private Action<Exception>? _failed;
        private uint _threadId;
        private int _disposed;

        internal WinEventSource() => _callback = OnEvent;

        public void Start(Action<nint> destroyed, Action<Exception> failed)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native window lifetimes require Windows.");
            _destroyed = destroyed;
            _failed = failed;
            _thread = new Thread(Run) { IsBackground = true, Name = "Native window lifetimes" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            _ready.Task.WaitAsync(CommandTimeout).GetAwaiter().GetResult();
        }

        public void EnsureProcess(int processId)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_hooks.ContainsKey(processId)) return;
            var request = new HookRequest(processId);
            _requests.Enqueue(request);
            // This message only dispatches an installation command. Its position relative
            // to asynchronous WinEvents supplies no lifecycle synchronization guarantee.
            if (!PostThreadMessage(_threadId, InstallMessage, 0, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot schedule a native window lifetime hook.");
            request.Completion.Task.WaitAsync(CommandTimeout).GetAwaiter().GetResult();
        }

        private void Run()
        {
            Exception? failure = null;
            try
            {
                // Calling PeekMessage creates the thread message queue before any commands
                // can be posted. The pump is separate from the worker's blocking stdin loop.
                PeekMessage(out _, 0, 0, 0, 0);
                _threadId = GetCurrentThreadId();
                _ready.TrySetResult();
                while (Volatile.Read(ref _disposed) == 0)
                {
                    var result = GetMessage(out var message, 0, 0, 0);
                    if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The native window lifetime message pump failed.");
                    if (result == 0) break;
                    if (message.Id == InstallMessage)
                    {
                        while (_requests.TryDequeue(out var request))
                        {
                            if (!_hooks.ContainsKey(request.ProcessId))
                            {
                                var hook = SetWinEventHook(DestroyEvent, DestroyEvent, 0, _callback,
                                    checked((uint)request.ProcessId), 0, 0);
                                if (hook == 0)
                                {
                                    var exception = new Win32Exception(Marshal.GetLastWin32Error(), "Cannot install a native window lifetime hook.");
                                    request.Completion.TrySetException(exception);
                                    throw exception;
                                }
                                _hooks[request.ProcessId] = hook;
                            }
                            request.Completion.TrySetResult();
                        }
                    }
                    else
                    {
                        TranslateMessage(ref message);
                        DispatchMessage(ref message);
                    }
                }
                if (Volatile.Read(ref _disposed) == 0)
                    throw new InvalidOperationException("The native window lifetime message pump stopped unexpectedly.");
            }
            catch (Exception exception)
            {
                failure = exception;
                _failed?.Invoke(exception);
                _ready.TrySetException(exception);
            }
            finally
            {
                foreach (var hook in _hooks.Values) UnhookWinEvent(hook);
                while (_requests.TryDequeue(out var request))
                    request.Completion.TrySetException(failure ?? new ObjectDisposedException(nameof(WinEventSource)));
                GC.KeepAlive(_callback);
            }
        }

        private void OnEvent(nint hook, uint eventId, nint handle, int objectId, int childId, uint eventThread, uint eventTime)
        {
            // Do not query the destroyed HWND: it may already identify another window.
            // Do not call UIA or wait here; hook callbacks can reenter.
            if (eventId != DestroyEvent || handle == 0 || objectId != 0 || childId != 0) return;
            try { _destroyed?.Invoke(handle); }
            catch (Exception exception) { _failed?.Invoke(exception); }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var thread = _thread;
            if (thread is null) return;
            if (_threadId != 0) PostThreadMessage(_threadId, QuitMessage, 0, 0);
            if (thread != Thread.CurrentThread) thread.Join(TimeSpan.FromSeconds(1));
        }

        private sealed record HookRequest(int ProcessId)
        {
            internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void WinEventCallback(nint hook, uint eventId, nint handle, int objectId, int childId, uint eventThread, uint eventTime);
        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            internal nint Handle;
            internal uint Id;
            internal nuint WParam;
            internal nint LParam;
            internal uint Time;
            internal int X;
            internal int Y;
            internal uint Private;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
            WinEventCallback callback, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(nint hook);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint remove);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll")]
        private static extern nint DispatchMessage(ref Message message);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
    }
}

internal interface INativeWindowLifetimeEventSource : IDisposable
{
    void Start(Action<nint> destroyed, Action<Exception> failed);
    void EnsureProcess(int processId);
}
