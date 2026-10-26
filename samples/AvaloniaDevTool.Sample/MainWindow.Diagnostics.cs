using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using AvaloniaDevTool.Agent;
using Microsoft.Extensions.Logging;

namespace AvaloniaDevTool.Sample;

public sealed partial class MainWindow
{
    private void InitializeResourceDemo()
    {
        Resources["DemoResource.Duplicate"] = "window local value";
        var nested = new ResourceDictionary { ["DemoResource.Duplicate"] = "nested value", [77] = "non-string key" };
        for (var index = 0; index < 125; index++)
            nested[$"DemoResource.Item{index:D3}"] = index;
        Resources.MergedDictionaries.Add(nested);
        Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary
            { ["DemoResource.Duplicate"] = "dark theme value" };
    }

    private void EmitStructuredLogsClick(object? sender, RoutedEventArgs args)
    {
        using (SampleDiagnostics.Orders.BeginScope(new Dictionary<string, object?>
                   { ["RequestId"] = "sample-order-7" }))
            SampleDiagnostics.Orders.LogWarning(new EventId(73, "SampleOrder"),
                new InvalidOperationException("Sample order failed", new ArgumentException("Invalid demo item")),
                "Order {Count} from {Source}; optional {Optional}",
                7,
                this,
                null);
        SampleDiagnostics.CustomSource.Publish(new DiagnosticsLogEvent("Warning",
            "Sample.Custom",
            "Custom item {Count}",
            [new("Count", 7), new("Optional", null)],
            Source: this));
        ViewModel.Status = "已发送真实 ILogger 和自定义源各一条结构化日志；参数 Count=7，含 scope、EventId、异常链和控件来源。";
    }
}
