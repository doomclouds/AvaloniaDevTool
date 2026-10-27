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
                    backend = NativeBackendFactory.Create(
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
                var error = NativeErrorMapper.Map(exception);
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint context);
}
