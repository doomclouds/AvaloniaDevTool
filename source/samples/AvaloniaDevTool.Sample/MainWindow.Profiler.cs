using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AvaloniaDevTool.Sample;

public sealed partial class MainWindow
{
    private void GenerateProfilerActivitiesClick(object? sender, RoutedEventArgs args)
    {
        var probe = new Button { Content = "Profiler probe", Classes = { "diagnosticTarget" } };
        DynamicChildren.Children.Add(probe);
        try
        {
            probe.ApplyTemplate();
            probe.Measure(new Size(300, 80));
            probe.Classes.Add("emphasized");
            _ = probe.FontWeight;
            probe.Classes.Remove("emphasized");
            _ = probe.FontWeight;
            var found = probe.TryFindResource("SampleWindowResource", out _);
            var missing = probe.TryFindResource("Sample.Profiler.MissingResource", out _);
            ViewModel.Status = $"已执行真实控件样式附加、类切换及资源查找；已有资源={found}，缺失资源={missing}。可读取性能采集结果。";
        }
        finally
        {
            DynamicChildren.Children.Remove(probe);
        }
    }
}
