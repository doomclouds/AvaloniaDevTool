using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using AvaloniaDevTool.Agent;

namespace AvaloniaDevTool.Previewer;

internal static class Program
{
    internal static PreviewOptions Options { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(
                "AvaloniaDevTool.Previewer --xaml ABSOLUTE.axaml [--pipe NAME] [--assembly ABSOLUTE.dll --project ABSOLUTE.csproj] [--application-assembly ABSOLUTE.dll] [--application-type TYPE] [--headless] [--no-watch]");
            return 0;
        }

        // Target initialization and constructors may log. Never contaminate the MCP parent's stdout.
        Console.SetOut(Console.Error);
        PreviewControl? control = null;
        try
        {
            Options = PreviewOptions.Parse(args);
            control = Options.ControlPipe is { } pipe ? new PreviewControl(pipe) : null;
            control?.ObserveOwner(() =>
            {
                // Owner loss must also stop a constructor or AppBuilder blocked before the UI loop.
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000).ConfigureAwait(false);
                    Environment.Exit(0);
                });
                Dispatcher.UIThread.Post(() => Dispatcher.UIThread.InvokeShutdown());
            });
            SetDesignMode();
            using var assembly = Options.ProjectPath is not null &&
                                 (Options.ApplicationAssemblyPath ?? Options.AssemblyPath) is { } path
                ? new PreviewAssemblyScope(path)
                : null;
            var builder = assembly is null
                ? BuildAvaloniaApp()
                : ConfigureTarget(assembly.Assembly, Options.ApplicationType);
            if (Options.Headless)
                builder.UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
            builder.SetupWithoutStarting();
            using var session =
                new PreviewSession(
                    new PreviewDocumentLoader(Options.XamlPath, Options.AssemblyPath, Options.ProjectPath),
                    Options.Watch);
            var window = new PreviewWindow(session, Options.PipeName);
            using var exit = new CancellationTokenSource();
            window.Closed += (_, _) => exit.Cancel();
            window.Show();
            if (session.Content is null && control is not null)
                throw new PreviewDocumentException("initial_preview_failed",
                    session.Error ?? "The source document could not be loaded.");
            using var agent = DiagnosticsAgent.Attach(builder.Instance!,
                new DiagnosticsAgentOptions
                {
                    PipeName = Options.PipeName,
                    ApplicationName = "XAML Preview · " + Path.GetFileName(Options.XamlPath),
                    ContentGenerationProvider = () => session.SuccessfulLoads,
                    TopLevelProvider = () => session.Content is Window document ? [window, document] : [window]
                });
            session.ContentReloaded += agent.NotifyContentChanged;
            control?.Report(new PreviewStartup(true, Environment.ProcessId, Options.PipeName, null));
            Dispatcher.UIThread.MainLoop(exit.Token);
            window.Close();
            return 0;
        }
        catch (Exception exception)
        {
            var error = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            var code = error is PreviewDocumentException document ? document.Code : "preview_start_failed";
            var message = $"{code}: {error.Message}";
            control?.Report(new PreviewStartup(false,
                Environment.ProcessId,
                Options?.PipeName ?? "",
                message[..Math.Min(message.Length, 4096)]));
            Console.Error.WriteLine(message);
            return 2;
        }
        finally
        {
            control?.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<PreviewApp>().UsePlatformDetect();

    private static AppBuilder ConfigureTarget(Assembly assembly, string? applicationType)
    {
        Type? type = applicationType is null ? assembly.EntryPoint?.DeclaringType : assembly.GetType(applicationType);
        if (applicationType is not null && type is null)
            throw new PreviewDocumentException("application_type_not_found",
                $"Type '{applicationType}' was not found in the selected assembly.");
        if (type is null)
        {
            var applications = assembly.GetTypes()
                .Where(candidate => typeof(Application).IsAssignableFrom(candidate) && !candidate.IsAbstract).ToArray();
            if (applications.Length == 1)
                type = applications[0];
        }

        var method = type?.GetMethod("BuildAvaloniaApp",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy,
            null,
            Type.EmptyTypes,
            null);
        if (method is not null && typeof(AppBuilder).IsAssignableFrom(method.ReturnType))
            return (AppBuilder)(method.Invoke(null, null) ??
                                throw new PreviewDocumentException("invalid_app_builder",
                                    "BuildAvaloniaApp returned null."));
        if (type is not null && typeof(Application).IsAssignableFrom(type) &&
            type.GetConstructor(Type.EmptyTypes) is not null)
            return AppBuilder.Configure(() => (Application)Activator.CreateInstance(type)!).UsePlatformDetect();
        throw new PreviewDocumentException("unsupported_project_startup",
            "The built application must expose parameterless static BuildAvaloniaApp(): AppBuilder, or select a parameterless Application type with applicationType. The application entry point is never invoked.");
    }

    private static void SetDesignMode()
    {
        // Avalonia's own RemoteDesignerEntryPoint uses this internal setter. Keep the bridge local,
        // version checked and fail closed instead of silently executing constructors in runtime mode.
        var version = typeof(Design).Assembly.GetName().Version;
        var setter = typeof(Design).GetProperty(nameof(Design.IsDesignMode))?.GetSetMethod(nonPublic: true);
        if (version is not { Major: 12, Minor: 1, Build: 4 } || setter is null)
            throw new PreviewDocumentException("unsupported_designer_api",
                $"The design mode bridge requires Avalonia 12.1.4; loaded {version}.");
        setter.Invoke(null, [true]);
    }
}

internal sealed record PreviewStartup(bool Ready, int ProcessId, string PipeName, string? Error);

internal sealed class PreviewControl : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly StreamWriter _writer;
    private readonly CancellationTokenSource _stop = new();
    private bool _reported;

    public PreviewControl(string name)
    {
        _pipe = new NamedPipeClientStream(".",
            name,
            PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            _pipe.ConnectAsync(_stop.Token).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
        }
        catch
        {
            _pipe.Dispose();
            _stop.Dispose();
            throw;
        }
    }

    public void Report(PreviewStartup startup)
    {
        if (_reported)
            return;
        _reported = true;
        try
        {
            _writer.WriteLine(JsonSerializer.Serialize(startup));
        }
        catch (IOException)
        {
        }
    }

    public void ObserveOwner(Action exited) => _ = Task.Run(async () =>
    {
        try
        {
            await _pipe.ReadAsync(new byte[1], _stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException
                                              or ObjectDisposedException)
        {
        }

        if (!_stop.IsCancellationRequested)
            exited();
    });

    public void Dispose()
    {
        _stop.Cancel();
        _writer.Dispose();
        _pipe.Dispose();
        // The reader task may still observe cancellation, so its source remains valid until exit.
    }
}
