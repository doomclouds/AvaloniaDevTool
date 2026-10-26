using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AvaloniaDevTool.NativeAutomation;

namespace AvaloniaDevTool.NativeAutomation.Worker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [MTAThread]
    private static void Main()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);
        // Process initialization is best effort; input uses an explicit thread scope.
        SetProcessDpiAwarenessContext(new nint(-4));
        NativeAutomationBackend? backend = null;
        while (Console.ReadLine() is { } line)
        {
            NativeWorkerRequest? request = null;
            NativeWorkerResponse response;
            try
            {
                request = JsonSerializer.Deserialize<NativeWorkerRequest>(line, JsonOptions)
                          ?? throw new NativeAutomationException("invalid_request", "A request is required.");
                object result;
                if (request.Command == "attach")
                {
                    if (backend is not null)
                        throw new NativeAutomationException("already_attached", "The worker already has a target.");
                    backend = new NativeAutomationBackend(
                        request.Payload.Deserialize<NativeWorkerAttach>(JsonOptions)!);
                    result = true;
                }
                else
                {
                    if (backend is null)
                        throw new NativeAutomationException("not_attached", "Attach a target first.");
                    result = backend.Dispatch(request.Command, request.Payload);
                }

                response = new NativeWorkerResponse(request.Id,
                    true,
                    JsonSerializer.SerializeToElement(result, result.GetType(), JsonOptions));
            }
            catch (Exception exception)
            {
                var error = MapError(exception);
                response = new NativeWorkerResponse(request?.Id ?? 0,
                    false,
                    ErrorCode: error.Code,
                    ErrorMessage: error.Message);
            }

            Console.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            Console.Out.Flush();
        }
        // A provider may still be executing a modal callback on a background thread.
        // Process exit releases COM without waiting indefinitely for that provider.
    }

    internal static NativeAutomationException MapError(Exception exception) => exception switch
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint context);
}
