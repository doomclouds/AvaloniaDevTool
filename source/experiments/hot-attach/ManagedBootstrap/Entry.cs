using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using AvaloniaDevTool.Agent;

namespace AvaloniaDevTool.AttachProbe;

public static class Entry
{
    private static object? _agent;
    private static int _busy;
    private static Func<AssemblyLoadContext, AssemblyName, Assembly?>? _resolver;

    // Snoop/CoreCLR ExecuteInDefaultAppDomain requires static int Method(string).
    public static int Run(string requestPath)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return 2;
        var request = JsonSerializer.Deserialize<Request>(File.ReadAllText(requestPath))!;
        var agentWasLoaded = AppDomain.CurrentDomain.GetAssemblies()
            .Any(assembly => assembly.GetName().Name == "AvaloniaDevTool.Agent");
        _resolver ??= (_, name) =>
        {
            if (name.Name is null || !(name.Name.StartsWith("AvaloniaDevTool.", StringComparison.Ordinal) ||
                                      name.Name == "Avalonia.Markup.Xaml.Loader"))
                return null;
            var path = Path.Combine(Path.GetDirectoryName(typeof(Entry).Assembly.Location)!, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        AssemblyLoadContext.Default.Resolving -= _resolver;
        AssemblyLoadContext.Default.Resolving += _resolver;
        _ = Task.Run(async () =>
        {
            try
            {
                if (request.Action == "stop")
                {
                    if (_agent is IAsyncDisposable asyncAgent)
                        await asyncAgent.DisposeAsync();
                    _agent = null;
                    AssemblyLoadContext.Default.Resolving -= _resolver;
                    Write(request, new { state = "stopped", processId = Environment.ProcessId });
                }
                else
                {
                    await StartCore(request, agentWasLoaded);
                }
            }
            catch (Exception error)
            {
                Write(request, new { state = "failed", error = error.ToString(), processId = Environment.ProcessId });
            }
            finally { Volatile.Write(ref _busy, 0); }
        });
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task StartCore(Request request, bool agentWasLoaded)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_agent is null)
                _agent = DiagnosticsAgent.Attach(Application.Current ?? throw new InvalidOperationException("No Application.Current"));
            Write(request, new
            {
                state = "started", processId = Environment.ProcessId, agentWasLoaded,
                avaloniaVersion = typeof(Application).Assembly.GetName().Version?.ToString(),
                applicationType = Application.Current?.GetType().FullName,
                pipeName = ((DiagnosticsAgent)_agent).PipeName,
                avaloniaAssemblyCopies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(assembly => assembly.GetName().Name == typeof(Application).Assembly.GetName().Name).Count()
            });
        }, DispatcherPriority.Normal, timeout.Token);
    }

    private static void Write(Request request, object result) =>
        File.WriteAllText(request.StatusPath, JsonSerializer.Serialize(result));

    private sealed record Request(string Action, string StatusPath);
}
