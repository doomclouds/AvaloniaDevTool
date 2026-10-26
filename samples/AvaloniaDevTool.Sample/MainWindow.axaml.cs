using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Logging;
using Avalonia.Media;

namespace AvaloniaDevTool.Sample;

public sealed partial class MainWindow : Window
{
    private int _nextNodeId = 1;
    private int _nextWindowId = 1;
    private int _logSequence;
    private bool _alternateResources;

    public SampleViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new SampleViewModel();
        InitializeComponent();
        DataContext = ViewModel;
        InitializeResourceDemo();
        InitializeCultureDemo();
        AddDynamicNode();
        AddDynamicNode();
        AddDynamicNode();
    }

    private async void CopyPipeClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel.IsDesignPreview)
        {
            ViewModel.Status = "当前为设计预览，请从预览宿主复制连接管道。";
            return;
        }

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                ViewModel.Status = "当前平台没有可用剪贴板，请手动选择并复制管道名称。";
                return;
            }

            await clipboard.SetTextAsync(ViewModel.PipeName);
            ViewModel.Status = "管道名称已复制，可用于诊断客户端连接。";
        }
        catch (Exception exception)
        {
            ViewModel.Status = $"复制失败：{exception.Message}";
        }
    }

    private void PreviewClick(object? sender, RoutedEventArgs e) =>
        ViewModel.Status = $"预览按钮收到点击，当前模式：{ViewModel.SelectedMode}，进度：{ViewModel.Progress:F0}%。";

    private void UpdateSourceClick(object? sender, RoutedEventArgs e) => ViewModel.UpdateBindingSource();

    private void ToggleClassClick(object? sender, RoutedEventArgs e)
    {
        if (!StyleTarget.Classes.Remove("emphasized"))
            StyleTarget.Classes.Add("emphasized");
        ViewModel.Status = $"样式 emphasized 当前为 {StyleTarget.Classes.Contains("emphasized")}，可重新查询样式信息。";
    }

    private void ChangeResourceClick(object? sender, RoutedEventArgs e)
    {
        _alternateResources = !_alternateResources;
        Application.Current!.Resources["SampleAccentBrush"] = Brush.Parse(_alternateResources ? "#0F8C79" : "#435DE7");
        Application.Current.Resources["SampleResourceText"] = $"应用主动更新资源 · {DateTime.Now:HH:mm:ss}";
        ViewModel.Status = "应用已主动改写资源；若诊断客户端曾修改同一项，恢复时应保留这个新值并报告冲突。";
    }

    private void EmitWarningClick(object? sender, RoutedEventArgs e)
    {
        Logger.TryGet(LogEventLevel.Warning, "Sample.Diagnostics")
            ?.Log(this, "Sample warning {Sequence}", ++_logSequence);
        ViewModel.Status = "已发送一条 Warning 日志。请先通过诊断接口开始日志 / 事件捕获。";
    }

    private void EmitInformationClick(object? sender, RoutedEventArgs e)
    {
        Logger.TryGet(LogEventLevel.Information, "Sample.Diagnostics")
            ?.Log(this, "Sample information {Sequence}", ++_logSequence);
        ViewModel.Status = "已发送 Information 日志；最低级别设为 Information 后可观察。";
    }

    private void RecordMetricsClick(object? sender, RoutedEventArgs e)
    {
        var depth = SampleMetrics.RecordBatch();
        ViewModel.Status = $"已记录 Counter +1、6 个合成 Histogram 样本；ObservableGauge 队列深度为 {depth}。";
    }

    private async void EmitBackgroundClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(() =>
            {
                for (var index = 1; index <= 30; index++)
                    Logger.TryGet(LogEventLevel.Warning, "Sample.Background")
                        ?.Log(null, "Background item {Index}", index);
            });
            ViewModel.Status = "已从后台线程发送 30 条 Warning 日志。";
        }
        catch (Exception exception)
        {
            ViewModel.Status = $"日志发送失败：{exception.Message}";
        }
    }

    private void AddNodeClick(object? sender, RoutedEventArgs e) => AddDynamicNode();

    private void AddDynamicNode()
    {
        var id = _nextNodeId++;
        var removeButton = new Button { Content = "移除", Padding = new Thickness(10, 5) };
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        content.Children.Add(new TextBlock
        {
            Text = $"动态节点 {id:00}",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Foreground = Brush.Parse("#354B93")
        });
        Grid.SetColumn(removeButton, 1);
        content.Children.Add(removeButton);
        var card = new Border
        {
            Name = $"DynamicNode{id}",
            Background = Brush.Parse(id % 2 == 0 ? "#F3F6FF" : "#F2F8F5"),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 9),
            Child = content
        };
        AutomationProperties.SetAutomationId(card, $"Sample.DynamicNode.{id}");
        removeButton.Click += (_, _) =>
        {
            DynamicChildren.Children.Remove(card);
            UpdateDynamicCount();
            ViewModel.Status = $"已移除动态节点 {id:00}。";
        };
        DynamicChildren.Children.Add(card);
        UpdateDynamicCount();
        ViewModel.Status = $"已添加动态节点 {id:00}。";
    }

    private void RemoveNodeClick(object? sender, RoutedEventArgs e)
    {
        if (DynamicChildren.Children.Count == 0)
        {
            ViewModel.Status = "动态节点已全部移除；可以再次添加。";
            return;
        }

        DynamicChildren.Children.RemoveAt(DynamicChildren.Children.Count - 1);
        UpdateDynamicCount();
        ViewModel.Status = "已移除最后一个动态节点。";
    }

    private void UpdateDynamicCount() => ViewModel.DynamicCount = DynamicChildren.Children.Count;

    private void OpenWindowClick(object? sender, RoutedEventArgs e)
    {
        var id = _nextWindowId++;
        var closeButton = new Button
            { Content = "关闭此窗口", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var window = new Window
        {
            Name = $"SampleSecondaryWindow{id}",
            Title = $"Avalonia 第二窗口 {id}",
            Width = 440,
            Height = 310,
            MinWidth = 360,
            MinHeight = 260,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(27),
                Spacing = 17,
                Children =
                {
                    new TextBlock { Text = "独立的顶层窗口", FontSize = 24, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "通过诊断客户端重新查询根列表，或开启拾取后点击这个窗口。", TextWrapping = TextWrapping.Wrap },
                    new TextBox { Name = $"SecondaryInput{id}", Text = "第二窗口中的输入框" },
                    new CheckBox { Content = "跨窗口属性检查", IsChecked = true },
                    closeButton
                }
            }
        };
        AutomationProperties.SetAutomationId(window, $"Sample.SecondaryWindow.{id}");
        closeButton.Click += (_, _) => window.Close();
        window.Show(this);
        ViewModel.Status = $"已打开第二窗口 {id}；诊断客户端查询的根列表应包含此窗口。";
    }
}
