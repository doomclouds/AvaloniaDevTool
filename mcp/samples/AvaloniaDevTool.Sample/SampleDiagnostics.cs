using AvaloniaDevTool.Agent;
using AvaloniaDevTool.Logging;
using Microsoft.Extensions.Logging;

namespace AvaloniaDevTool.Sample;

internal static class SampleDiagnostics
{
    public static DiagnosticsLoggerProvider Provider { get; } = new();
    public static DiagnosticsLogSource CustomSource { get; } = new();
    private static readonly ILoggerFactory Factory = LoggerFactory.Create(builder => builder.AddProvider(Provider));
    public static ILogger Orders { get; } = Factory.CreateLogger("Sample.Orders");
    public static void Dispose() => Factory.Dispose();
}
