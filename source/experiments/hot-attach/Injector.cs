#:property PublishAot=false

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
    throw new PlatformNotSupportedException("Windows x64 only");
if (args.Length != 6)
    throw new ArgumentException("PID expectedStartTicks NativeBootstrap.dll ManagedBootstrap.dll request.json expectedProcessName");
using var target = Process.GetProcessById(int.Parse(args[0]));
if (target.StartTime.ToUniversalTime().Ticks != long.Parse(args[1]))
    throw new InvalidOperationException("Target identity changed");
if (target.ProcessName != args[5])
    throw new InvalidOperationException("Target process name changed");
var nativePath = Path.GetFullPath(args[2]);
var managedPath = Path.GetFullPath(args[3]);
var requestPath = Path.GetFullPath(args[4]);
if (!File.Exists(nativePath) || !File.Exists(managedPath) || !File.Exists(requestPath))
    throw new FileNotFoundException("Probe input missing");
var process = Native.OpenProcess(0x043A, false, target.Id);
if (process == IntPtr.Zero) throw new Win32Exception();
try
{
    var localKernel = Native.GetModuleHandleW("kernel32.dll");
    var localLoad = Native.GetProcAddress(localKernel, "LoadLibraryW");
    if (!Native.GetModuleHandleExW(6, localLoad, out var owner)) throw new Win32Exception();
    var moduleName = new StringBuilder(32768);
    if (Native.GetModuleFileNameW(owner, moduleName, moduleName.Capacity) == 0) throw new Win32Exception();
    target.Refresh();
    var remoteOwner = target.Modules.Cast<ProcessModule>()
        .Single(module => module.ModuleName.Equals(Path.GetFileName(moduleName.ToString()), StringComparison.OrdinalIgnoreCase));
    var remoteLoad = remoteOwner.BaseAddress + (localLoad - owner);
    CallRemote(remoteLoad, nativePath, false);
    target.Refresh();
    var remoteModule = target.Modules.Cast<ProcessModule>().Single(module =>
        Path.GetFullPath(module.FileName).Equals(nativePath, StringComparison.OrdinalIgnoreCase));
    var localModule = NativeLibrary.Load(nativePath);
    try
    {
        var remoteEntry = remoteModule.BaseAddress + (NativeLibrary.GetExport(localModule, "Bootstrap") - localModule);
        CallRemote(remoteEntry, managedPath + "\nAvaloniaDevTool.AttachProbe.Entry\nRun\n" + requestPath, true);
    }
    finally { NativeLibrary.Free(localModule); }
    Console.WriteLine("Bootstrap request accepted; inspect the status file for completion.");
}
finally { Native.CloseHandle(process); }

void CallRemote(IntPtr entry, string text, bool checkResult)
{
    var bytes = Encoding.Unicode.GetBytes(text + "\0");
    var memory = Native.VirtualAllocEx(process, IntPtr.Zero, (nuint)bytes.Length, 0x3000, 4);
    if (memory == IntPtr.Zero) throw new Win32Exception();
    var completed = true;
    try
    {
        if (!Native.WriteProcessMemory(process, memory, bytes, (nuint)bytes.Length, out var written) || written != (nuint)bytes.Length)
            throw new Win32Exception();
        var thread = Native.CreateRemoteThread(process, IntPtr.Zero, 0, entry, memory, 0, out _);
        if (thread == IntPtr.Zero) throw new Win32Exception();
        try
        {
            completed = false;
            if (Native.WaitForSingleObject(thread, 15000) != 0)
                throw new TimeoutException("Remote execution outcome unknown; do not retry automatically.");
            completed = true;
            if (!Native.GetExitCodeThread(thread, out var result)) throw new Win32Exception();
            if (checkResult && result != 0) throw new InvalidOperationException($"Bootstrap failed: 0x{result:X8}");
        }
        finally { Native.CloseHandle(thread); }
    }
    finally { if (completed) Native.VirtualFreeEx(process, memory, 0, 0x8000); }
}

internal static class Native
{
    [DllImport("kernel32", SetLastError = true)] internal static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
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
