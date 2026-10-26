using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using AvaloniaDevTool.Client;
using AvaloniaDevTool.Logging;
using AvaloniaDevTool.Protocol;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AvaloniaDevTool.Agent.Tests;

public sealed class StructuredLoggingTests
{
    [AvaloniaFact]
    public async Task Agent_and_client_preserve_raw_template_typed_values_and_filter_before_history()
    {
        var source = new DiagnosticsLogSource();
        var target = new Button { Name = "LogOrigin" };
        var window = new Window { Content = target };
        window.Show();
        try
        {
            await using var agent = CreateAgent(window, source);
            await using var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            await client.StartActivityAsync(new ActivityStartRequest(MinimumLogLevel: "Information",
                LogFilter: new LogFilter("orders", "accepted", "Amount", "12")));
            Assert.True(source.HasSubscribers);
            await Task.Run(() =>
            {
                for (var index = 0; index < 650; index++)
                    source.Publish(new("Warning",
                        "Orders",
                        "Rejected {Amount}",
                        [new("Amount", 12)]));
                source.Publish(new("Warning",
                    "Orders",
                    "Accepted {Amount:0.0} for {Customer}; {Missing}; {State}",
                    [
                        new("Amount", 12.25m), new("Customer", "A"), new("Missing", null),
                        new("State", new UnsafeValue())
                    ],
                    Source: target));
            });
            var entry = Assert.Single(await client.GetActivityAsync());
            Assert.Equal("Accepted 12.3 for A; (null); [UnsafeValue: unavailable]", entry.Message);
            Assert.NotNull(entry.ObjectId);
            var details = Assert.IsType<LogDetails>(entry.LogDetails);
            Assert.Equal("Accepted {Amount:0.0} for {Customer}; {Missing}; {State}", details.MessageTemplate);
            Assert.Equal(LogValueState.Known, details.ParametersState);
            Assert.Equal("System.Decimal", details.Parameters[0].TypeName);
            Assert.Equal("12.25", details.Parameters[0].Value);
            Assert.Equal(LogValueState.Null, details.Parameters[2].State);
            Assert.Equal(LogValueState.Unavailable, details.Parameters[3].State);
            Assert.Equal(0, (await client.GetActivityStatusAsync()).DroppedCount);
            var properties = await client.GetPropertiesAsync(entry.ObjectId!.Value);
            Assert.Contains(properties, property => property.Name == "Name" && property.Value == "LogOrigin");
            await client.StopActivityAsync();
            Assert.False(source.HasSubscribers);
            source.Publish(new("Error", "Orders", "Accepted after stop", []));
            Assert.Single(await client.GetActivityAsync());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Real_logger_factory_maps_original_format_event_id_scope_level_and_exception()
    {
        using var provider = new DiagnosticsLoggerProvider();
        using var factory =
            LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        var window = new Window();
        window.Show();
        try
        {
            await using var agent = CreateAgent(window, provider);
            await using var client = new DiagnosticsClient();
            await client.ConnectAsync(agent.PipeName);
            await client.StartActivityAsync(new ActivityStartRequest(MinimumLogLevel: "Verbose"));
            var logger = factory.CreateLogger("Application.Orders");
            await Task.Run(() =>
            {
                using (logger.BeginScope(new Dictionary<string, object?> { ["Tenant"] = 42 }))
                    logger.LogCritical(new EventId(71, "SaveFailed"),
                        new InvalidOperationException("cannot save", new Exception("inner")),
                        "Save {Count} for {User}",
                        3,
                        "alice");
            });
            var entry = Assert.Single(await client.GetActivityAsync(), item => item.Category == "Application.Orders");
            Assert.Equal("Fatal", entry.Level);
            Assert.Equal("Save 3 for alice", entry.Message);
            var details = Assert.IsType<LogDetails>(entry.LogDetails);
            Assert.Equal("Microsoft.Extensions.Logging", details.Origin);
            Assert.Equal("Save {Count} for {User}", details.MessageTemplate);
            Assert.Equal(71, details.EventId);
            Assert.Equal("SaveFailed", details.EventName);
            Assert.Equal("System.Int32", details.Parameters[0].TypeName);
            Assert.Equal("cannot save", details.Exception!.Message);
            Assert.Equal("inner", Assert.Single(details.Exception.InnerExceptions).Message);
            Assert.Equal("42", Assert.Single(Assert.Single(details.Scopes!).Parameters).Value);
            await client.StopActivityAsync();
            Assert.False(logger.IsEnabled(LogLevel.Warning));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Snapshot_limits_parameters_templates_and_safe_exception_graphs_without_business_formatting()
    {
        var source = new DiagnosticsLogSource();
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { }, [source]);
        service.Start(new ActivityStartRequest());
        var unsafeValue = new UnsafeValue();
        var exception = new Exception("root");
        typeof(Exception).GetField("_innerException", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(
            exception,
            exception);
        source.Publish(new("Error",
            "Bounds",
            new string('t', 6000),
            Enumerable.Range(0, 80).Select(index =>
                    new KeyValuePair<string, object?>("P" + index, index == 0 ? unsafeValue : new string('x', 2000)))
                .ToArray(),
            new AggregateException(Enumerable.Repeat(exception, 100))));
        var entry = Assert.Single(service.Snapshot());
        var details = Assert.IsType<LogDetails>(entry.LogDetails);
        Assert.Equal(32, details.Parameters.Count);
        Assert.True(details.ParametersTruncated);
        Assert.True(details.TemplateTruncated);
        Assert.Equal(4097, details.MessageTemplate!.Length);
        Assert.Equal(1025, details.Parameters[1].Value!.Length);
        Assert.True(details.Exception!.IsTruncated);
        Assert.True(CountExceptions(details.Exception) <= 9);
        Assert.Contains(details.Exception.InnerExceptions,
            item => item.IsCycle || item.InnerExceptions.Any(child => child.IsCycle));
        Assert.Equal(0, unsafeValue.Calls);
    }

    [AvaloniaFact]
    public void Custom_exception_properties_are_not_executed_and_parameter_availability_remains_precise()
    {
        var source = new DiagnosticsLogSource();
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { }, [source]);
        service.Start(new ActivityStartRequest());
        var exception = new UnsafeException();
        source.Publish(new("Error", "States", null, Exception: exception));
        source.Publish(new("Warning", "States", "No parameters", []));
        var entries = service.Snapshot();
        Assert.Equal(LogValueState.Unavailable, entries[0].LogDetails!.ParametersState);
        Assert.Null(entries[0].LogDetails!.MessageTemplate);
        Assert.Equal(LogValueState.Known, entries[1].LogDetails!.ParametersState);
        Assert.Empty(entries[1].LogDetails!.Parameters);
        Assert.Equal(0, exception.Calls);
    }

    [AvaloniaFact]
    public async Task Queued_and_historical_logs_do_not_retain_sources_arguments_or_exceptions()
    {
        var source = new DiagnosticsLogSource();
        using var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { }, [source]);
        service.Start(new ActivityStartRequest());
        var references = await Task.Run(() => PublishTransient(source));
        Collect();
        Assert.All(references, reference => Assert.False(reference.IsAlive));
        var entry = Assert.Single(service.Snapshot());
        Assert.Null(entry.ObjectId);
        Assert.NotNull(entry.LogDetails!.Exception);
        Collect();
        Assert.All(references, reference => Assert.False(reference.IsAlive));
    }

    [AvaloniaFact]
    public void Custom_source_subscription_and_capture_stop_with_disposal()
    {
        var source = new DiagnosticsLogSource();
        var service = new ActivityService(new WeakObjectRegistry(), (_, _) => { }, [source]);
        service.Start(new ActivityStartRequest());
        source.Publish(new("Warning", "Lifecycle", "Before", []));
        Assert.Single(service.Snapshot());
        Assert.True(source.HasSubscribers);
        service.Dispose();
        Assert.False(source.HasSubscribers);
        source.Publish(new("Error", "Lifecycle", "After", []));
    }

    [AvaloniaFact]
    public void Reentrant_previous_sink_and_notification_logging_do_not_recurse_or_fill_history()
    {
        var original = Avalonia.Logging.Logger.Sink;
        var previous = new ReentrantSink();
        Avalonia.Logging.Logger.Sink = previous;
        try
        {
            using var service = new ActivityService(new WeakObjectRegistry(),
                (method, _) =>
                {
                    if (method == ActivityMethods.EntryAdded)
                        Avalonia.Logging.Logger.Sink!.Log(LogEventLevel.Warning, "Observer", null, "Do not recapture");
                });
            service.Start(new ActivityStartRequest());
            Avalonia.Logging.Logger.Sink!.Log(LogEventLevel.Warning, "Reentry", null, "One");
            Assert.Single(service.Snapshot());
            Assert.Single(service.Snapshot());
            service.Stop();
            Assert.Same(previous, Avalonia.Logging.Logger.Sink);
        }
        finally
        {
            Avalonia.Logging.Logger.Sink = original;
        }
    }

    [AvaloniaFact]
    public void Nested_capture_wrappers_forward_to_original_sink_and_restore_after_out_of_order_stop()
    {
        var original = Avalonia.Logging.Logger.Sink;
        var previous = new CountingSink();
        Avalonia.Logging.Logger.Sink = previous;
        try
        {
            using var first = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
            using var second = new ActivityService(new WeakObjectRegistry(), (_, _) => { });
            first.Start(new ActivityStartRequest());
            second.Start(new ActivityStartRequest());
            Avalonia.Logging.Logger.Sink!.Log(LogEventLevel.Warning, "Nested", null, "Both capture");
            Assert.Single(first.Snapshot());
            Assert.Single(second.Snapshot());
            Assert.Equal(1, previous.Count);
            first.Stop();
            Avalonia.Logging.Logger.Sink!.Log(LogEventLevel.Warning, "Nested", null, "Second only");
            Assert.Single(first.Snapshot());
            Assert.Equal(2, second.Snapshot().Count);
            Assert.Equal(2, previous.Count);
            second.Stop();
            Assert.Same(previous, Avalonia.Logging.Logger.Sink);
        }
        finally
        {
            Avalonia.Logging.Logger.Sink = original;
        }
    }

    private static DiagnosticsAgent CreateAgent(Window window, IDiagnosticsLogSource source) =>
        DiagnosticsAgent.Attach(Application.Current!,
            new DiagnosticsAgentOptions
            {
                PipeName = "structured-logs-" + Guid.NewGuid().ToString("N"), TopLevelProvider = () => [window],
                LogSources = [source]
            });

    private static int CountExceptions(LogExceptionInfo error) => 1 + error.InnerExceptions.Sum(CountExceptions);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] PublishTransient(DiagnosticsLogSource source)
    {
        var origin = new object();
        var argument = new UnsafeValue();
        var error = new InvalidOperationException("Transient exception");
        source.Publish(new("Error", "Weak", "Temporary {Value}", [new("Value", argument)], error, origin));
        return [new(origin), new(argument), new(error)];
    }

    private static void Collect()
    {
        for (var index = 0; index < 3; index++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class UnsafeValue : IFormattable
    {
        public int Calls { get; private set; }

        public override string ToString()
        {
            Calls++;
            throw new InvalidOperationException("Business ToString invoked");
        }

        public string ToString(string? format, IFormatProvider? provider) => ToString();
    }

    private sealed class UnsafeException : Exception
    {
        public int Calls { get; private set; }

        public override string Message
        {
            get
            {
                Calls++;
                throw new InvalidOperationException("Unsafe Message invoked");
            }
        }

        public override string? StackTrace
        {
            get
            {
                Calls++;
                throw new InvalidOperationException("Unsafe StackTrace invoked");
            }
        }
    }

    private sealed class ReentrantSink : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) => true;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Avalonia.Logging.Logger.Sink?.Log(level, area, source, "Reentrant");

        public void Log(LogEventLevel level,
            string area,
            object? source,
            string messageTemplate,
            params object?[] propertyValues) =>
            Log(level, area, source, messageTemplate);
    }

    private sealed class CountingSink : ILogSink
    {
        public int Count { get; private set; }
        public bool IsEnabled(LogEventLevel level, string area) => true;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Count++;

        public void Log(LogEventLevel level,
            string area,
            object? source,
            string messageTemplate,
            params object?[] propertyValues) => Count++;
    }
}
