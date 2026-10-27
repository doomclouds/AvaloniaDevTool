using System.Runtime.InteropServices;
using AvaloniaDevTool.NativeAutomation;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal static class NativeErrorMapper
{
    internal static NativeAutomationException Map(Exception exception) => exception switch
    {
        NativeAutomationException native => native,
        FlaUI.Core.Exceptions.ElementNotAvailableException => new("stale_node", "The native element no longer exists."),
        FlaUI.Core.Exceptions.PatternNotSupportedException => new("unsupported_pattern",
            "The native element does not support this action."),
        COMException com when com.HResult == unchecked((int)0x80040201) => new("stale_node",
            "The native element no longer exists."),
        COMException com when com.HResult == unchecked((int)0x80131505) => new("provider_timeout",
            "The Windows UI Automation provider timed out."),
        COMException com when com.HResult == unchecked((int)0x80070005) => new("access_denied",
            "Windows denied access to the native element."),
        UnauthorizedAccessException => new("access_denied", "Windows denied access to the native element."),
        ArgumentException => new("invalid_argument", exception.Message),
        TimeoutException => new("provider_timeout", "The Windows UI Automation provider timed out."),
        _ => new("native_error",
            $"{exception.GetType().Name} (HRESULT=0x{exception.HResult:X8}, Win32={(exception is System.ComponentModel.Win32Exception win32 ? win32.NativeErrorCode : null)}): {exception.Message}")
    };
}
