using System.Diagnostics.Metrics;
using System.Reflection;
using Avalonia;
using AvaloniaDevTool.Protocol;

namespace AvaloniaDevTool.Agent;

/// <summary>Isolates the verified 12.1.4 late-init path without changing the cached diagnostics switch.</summary>
internal static class Avalonia12MetricsAdapter
{
    private static readonly object Gate = new();
    public const string MeterName = "Avalonia.Diagnostic.Meter";

    public static AvaloniaMetricsSupportDto EnsureInitialized()
    {
        lock (Gate)
        {
            var assembly = typeof(AvaloniaObject).Assembly;
            var version = assembly.GetName().Version;
            if (version is null || version.Major != 12 || version.Minor != 1 || version.Build != 4)
                return new(false,
                    false,
                    version?.ToString() ?? "unknown",
                    MeterName,
                    "Late initialization is verified only for Avalonia 12.1.4. Enable Avalonia.Diagnostics.Diagnostic.IsEnabled before framework startup.");
            var diagnostic = assembly.GetType("Avalonia.Diagnostics.Diagnostic");
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var histogram = diagnostic?.GetField("s_layoutMeasure", flags);
            var initialize = diagnostic?.GetMethod("InitMetrics", flags, Type.EmptyTypes);
            var enabled = diagnostic?.GetProperty("IsEnabled", flags);
            if (histogram?.FieldType != typeof(Histogram<double>) || initialize is null ||
                enabled?.PropertyType != typeof(bool))
                return new(false,
                    false,
                    version.ToString(),
                    MeterName,
                    "The framework metrics initialization shape is unavailable.");
            try
            {
                var cached = enabled.GetValue(null) is true;
                // Reading the field completes the framework static initializer. Never replace existing instruments.
                if (histogram.GetValue(null) is null)
                    initialize.Invoke(null, null);
                return new(histogram.GetValue(null) is Histogram<double>, cached, version.ToString(), MeterName);
            }
            catch (Exception error) when (error is TargetInvocationException or MemberAccessException
                                              or TypeInitializationException)
            {
                return new(false,
                    false,
                    version.ToString(),
                    MeterName,
                    "Framework metrics initialization failed: " + error.GetType().Name);
            }
        }
    }
}
