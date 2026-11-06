using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace AvaloniaDevTool.Bootstrap;

/// <summary>Runtime-only bootstrap: it deliberately has no Agent or Avalonia assembly reference.</summary>
public static class Entry
{
    private static object? _agent;
    private static int _starting;
    private static string? _agentDirectory;
    private static bool _resolverRegistered;

    public static int Run(string requestPath)
    {
        var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath))!;
        if (request.Action == "probe")
        {
            var application = FindType("Avalonia.Application");
            Write(request, new
            {
                state = application is null ? "not_avalonia" : "probed",
                processId = Environment.ProcessId,
                runtimeVersion = Environment.Version.ToString(),
                avaloniaVersion = application?.Assembly.GetName().Version?.ToString(3)
            });
            return 0;
        }
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
            return 2;
        _ = Task.Run(async () =>
        {
            try
            {
                if (request.Action == "stop")
                {
                    if (_agent is IAsyncDisposable disposable)
                        await disposable.DisposeAsync();
                    _agent = null;
                    Write(request, new { state = "stopped", processId = Environment.ProcessId });
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
                Write(request, new
                {
                    state = "started", processId = Environment.ProcessId,
                    pipeName = _agent!.GetType().GetProperty("PipeName")!.GetValue(_agent)
                });
            }
            catch (Exception error)
            {
                Write(request, new { state = "failed", processId = Environment.ProcessId, error = error.GetBaseException().Message });
            }
            finally { Volatile.Write(ref _starting, 0); }
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

    private static void Write(Request request, object result)
    {
        var temporary = request.StatusPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result));
        File.Move(temporary, request.StatusPath, true);
    }

    private sealed record Request(string Action, string StatusPath, string? AgentPath);
}
