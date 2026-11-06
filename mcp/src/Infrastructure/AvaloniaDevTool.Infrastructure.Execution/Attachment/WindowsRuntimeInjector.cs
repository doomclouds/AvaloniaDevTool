using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AvaloniaDevTool.Client.Attachment;

internal static class WindowsRuntimeInjector
{
    internal static void Invoke(int processId, DateTimeOffset expectedStartTime, string nativePath,
        string assemblyPath, string requestPath)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("unsupported_platform: Native attachment requires Windows x64.");
        using var target = Process.GetProcessById(processId);
        if (target.StartTime.ToUniversalTime() != expectedStartTime.UtcDateTime)
            throw new InvalidOperationException("stale_process: The selected process identity changed.");
        var process = Win32.OpenProcess(0x043A, false, processId);
        if (process == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "attach_access_denied: The process could not be opened.");
        try
        {
            if (!Win32.IsWow64Process2(process, out var machine, out var nativeMachine) || machine != 0 || nativeMachine != 0x8664)
                throw new PlatformNotSupportedException("unsupported_architecture: The target must be native Windows x64.");
            target.Refresh();
            var module = target.Modules.Cast<ProcessModule>().FirstOrDefault(item =>
                Path.GetFullPath(item.FileName).Equals(nativePath, StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                var localLoad = Win32.GetProcAddress(Win32.GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
                if (!Win32.GetModuleHandleExW(6, localLoad, out var owner))
                    throw new Win32Exception();
                var name = new StringBuilder(32768);
                if (Win32.GetModuleFileNameW(owner, name, name.Capacity) == 0)
                    throw new Win32Exception();
                var remoteOwner = target.Modules.Cast<ProcessModule>().Single(item =>
                    item.ModuleName.Equals(Path.GetFileName(name.ToString()), StringComparison.OrdinalIgnoreCase));
                CallRemote(remoteOwner.BaseAddress + (localLoad - owner), nativePath, false);
                target.Refresh();
                module = target.Modules.Cast<ProcessModule>().Single(item =>
                    Path.GetFullPath(item.FileName).Equals(nativePath, StringComparison.OrdinalIgnoreCase));
            }
            var local = NativeLibrary.Load(nativePath);
            try
            {
                var entry = module.BaseAddress + (NativeLibrary.GetExport(local, "Bootstrap") - local);
                CallRemote(entry, assemblyPath + "\nAvaloniaDevTool.Bootstrap.Entry\nRun\n" + requestPath, true);
            }
            finally { NativeLibrary.Free(local); }
        }
        finally { Win32.CloseHandle(process); }

        void CallRemote(IntPtr entry, string text, bool checkResult)
        {
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var memory = Win32.VirtualAllocEx(process, IntPtr.Zero, (nuint)bytes.Length, 0x3000, 4);
            if (memory == IntPtr.Zero)
                throw new Win32Exception();
            var completed = true;
            try
            {
                if (!Win32.WriteProcessMemory(process, memory, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
                    throw new Win32Exception();
                var thread = Win32.CreateRemoteThread(process, IntPtr.Zero, 0, entry, memory, 0, out _);
                if (thread == IntPtr.Zero)
                    throw new Win32Exception();
                try
                {
                    completed = false;
                    if (Win32.WaitForSingleObject(thread, 15000) != 0)
                        throw new TimeoutException("attach_outcome_unknown: Remote execution is still unconfirmed; do not reinject automatically.");
                    completed = true;
                    if (!Win32.GetExitCodeThread(thread, out var result))
                        throw new Win32Exception();
                    if (checkResult && result != 0)
                        throw new InvalidOperationException($"native_bootstrap_failed: HRESULT 0x{result:X8}.");
                }
                finally { Win32.CloseHandle(thread); }
            }
            finally { if (completed) Win32.VirtualFreeEx(process, memory, 0, 0x8000); }
        }
    }

    private static class Win32
    {
        [DllImport("kernel32", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32", SetLastError = true)] internal static extern bool IsWow64Process2(IntPtr process, out ushort machine, out ushort nativeMachine);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32", SetLastError = true)] internal static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32", SetLastError = true)] internal static extern bool GetModuleHandleExW(uint flags, IntPtr address, out IntPtr module);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern uint GetModuleFileNameW(IntPtr module, StringBuilder text, int length);
        [DllImport("kernel32", SetLastError = true)] internal static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint kind, uint protect);
        [DllImport("kernel32", SetLastError = true)] internal static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint kind);
        [DllImport("kernel32", SetLastError = true)] internal static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] data, nuint length, out nuint written);
        [DllImport("kernel32", SetLastError = true)] internal static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, nuint stackSize, IntPtr start, IntPtr argument, uint flags, out uint id);
        [DllImport("kernel32", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32", SetLastError = true)] internal static extern bool GetExitCodeThread(IntPtr thread, out uint result);
        [DllImport("kernel32")] internal static extern bool CloseHandle(IntPtr handle);
    }
}
