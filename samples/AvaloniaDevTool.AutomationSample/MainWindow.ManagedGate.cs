using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;

namespace AvaloniaDevTool.AutomationSample;

public sealed partial class MainWindow
{
    private readonly TextBlock _managedGateStatus = Label("ManagedGateStatus", "Ready");

    private Control BuildManagedGateControls() => Row(
        ActionButton("BlockManagedGateButton", "Block managed RPC with a native OK dialog", BlockManagedGate), _managedGateStatus);

    private void BlockManagedGate()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The managed gate acceptance fixture requires Windows.");
        var owner = this.TryGetPlatformHandle();
        if (owner is not { HandleDescriptor: "HWND", Handle: not 0 })
            throw new InvalidOperationException("The managed gate dialog requires the main window's real HWND.");

        _managedGateStatus.Text = "Entered";
        // Remain inside this Click handler and its managed RPC until the real native dialog is answered.
        // MessageBoxW runs its native modal message loop; no background task or synthetic delay releases the gate.
        var result = ManagedGateMessageBox(owner.Handle,
            "The original managed click remains active until this owned dialog is answered.",
            "Automation sample - Managed gate", 0);
        if (result != 1)
        {
            _managedGateStatus.Text = $"Failed: MessageBoxW returned {result}";
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The managed gate's standard OK dialog did not return IDOK.");
        }
        _managedGateStatus.Text = "Returned";
    }

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int ManagedGateMessageBox(nint owner, string text, string caption, uint type);
}
