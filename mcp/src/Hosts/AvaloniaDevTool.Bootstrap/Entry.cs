using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;

namespace AvaloniaDevTool.Bootstrap;

/// <summary>Runtime-only bootstrap: it deliberately has no Agent or Avalonia assembly reference.</summary>
public static class Entry
{
    private static object? _agent;
    private static int _starting;
    private static string? _agentDirectory;
    private static bool _resolverRegistered;

    public static int Run(string requestJson)
    {
        var request = JsonSerializer.Deserialize<Request>(requestJson)!;
        var exclusive = request.Action != "probe";
        if (exclusive && Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
            return 2;
        _ = Task.Run(async () =>
        {
            try
            {
                if (request.Action == "probe")
                {
                    var application = FindType("Avalonia.Application");
                    await WriteAsync(request, new
                    {
                        state = application is null ? "not_avalonia" : "probed",
                        processId = Environment.ProcessId,
                        runtimeVersion = Environment.Version.ToString(),
                        avaloniaVersion = application?.Assembly.GetName().Version?.ToString(3)
                    });
                    return;
                }
                if (request.Action == "stop")
                {
                    if (_agent is IAsyncDisposable disposable)
                        await disposable.DisposeAsync();
                    _agent = null;
                    await WriteAsync(request, new { state = "stopped", processId = Environment.ProcessId });
                    return;
                }
                _agentDirectory = Path.GetDirectoryName(request.AgentPath ?? throw new ArgumentException("AgentPath is required"));
                if (!_resolverRegistered)
                {
                    AssemblyLoadContext.Default.Resolving += ResolveAgent;
                    _resolverRegistered = true;
                }
                var dispatcherType = FindType("Avalonia.Threading.Dispatcher") ?? throw new InvalidOperationException("Avalonia dispatcher is unavailable.");
                var dispatcher = dispatcherType.GetProperty("UIThread")!.GetValue(null);
                var post = dispatcherType.GetMethods().Single(method => method.Name == "Post" &&
                    method.GetParameters() is var parameters && parameters.Length == 2 && parameters[0].ParameterType == typeof(Action));
                var priority = post.GetParameters()[1].ParameterType.GetField("Normal")!.GetValue(null);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Action start = () =>
                {
                    if (timeout.IsCancellationRequested)
                    {
                        completed.TrySetCanceled();
                        return;
                    }
                    try
                    {
                        if (_agent is null)
                        {
                            var application = FindType("Avalonia.Application")!.GetProperty("Current")!.GetValue(null)
                                ?? throw new InvalidOperationException("Application.Current is not ready.");
                            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item => item.GetName().Name == "AvaloniaDevTool.Agent")
                                ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(request.AgentPath!);
                            _agent = assembly.GetType("AvaloniaDevTool.Agent.DiagnosticsAgent")!.GetMethod("Attach")!
                                .Invoke(null, [application, null]);
                        }
                        completed.TrySetResult();
                    }
                    catch (Exception error) { completed.TrySetException(error); }
                };
                post.Invoke(dispatcher, [start, priority]);
                await completed.Task.WaitAsync(timeout.Token);
                await WriteAsync(request, new
                {
                    state = "started", processId = Environment.ProcessId,
                    pipeName = _agent!.GetType().GetProperty("PipeName")!.GetValue(_agent)
                });
            }
            catch (Exception error)
            {
                await WriteAsync(request, new { state = "failed", processId = Environment.ProcessId, error = error.GetBaseException().Message });
            }
            finally { if (exclusive) Volatile.Write(ref _starting, 0); }
        });
        return 0;
    }

    private static Type? FindType(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .Select(assembly => assembly.GetType(name, false)).FirstOrDefault(type => type is not null);

    private static Assembly? ResolveAgent(AssemblyLoadContext context, AssemblyName name)
    {
        if (_agentDirectory is null || name.Name is null ||
            !(name.Name.StartsWith("AvaloniaDevTool.", StringComparison.Ordinal) || name.Name == "Avalonia.Markup.Xaml.Loader"))
            return null;
        var path = Path.Combine(_agentDirectory, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    private static async Task WriteAsync(Request request, object result)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", request.ReplyPipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(timeout.Token);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            await writer.WriteLineAsync(JsonSerializer.Serialize(result).AsMemory(), timeout.Token);
            await writer.FlushAsync(timeout.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or TimeoutException)
        {
            // The requesting host has gone away. Never propagate its IPC failure into the application.
        }
    }

    private sealed record Request(string Action, string ReplyPipeName, string? AgentPath);
}
