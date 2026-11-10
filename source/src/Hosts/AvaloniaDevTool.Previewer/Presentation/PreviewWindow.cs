using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AvaloniaDevTool.Previewer;

public sealed class PreviewWindow : Window
{
    private readonly PreviewSession _session;
    private readonly ContentControl _viewport;
    private readonly TextBlock _status;
    private readonly TextBlock _error;
    private readonly TextBlock _windowNotice;
    private Window? _documentWindow;

    public PreviewWindow(PreviewSession session, string pipeName)
    {
        _session = session;
        Title = "Avalonia XAML Preview · " + Path.GetFileName(session.FilePath);
        Width = 980;
        Height = 760;
        MinWidth = 640;
        MinHeight = 480;
        Background = Brush.Parse("#F3F5F9");
        Name = "PreviewHostWindow";

        var source = new TextBlock
        {
            Name = "PreviewSourcePath", Text = session.FilePath, Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13, Margin = new Thickness(0, 6, 0, 14)
        };
        AutomationProperties.SetAutomationId(source, "Previewer.Source");
        var pipe = new TextBox
        {
            Name = "PreviewPipeName", Text = pipeName, IsReadOnly = true, MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(pipe, "Previewer.Pipe");
        var reload = new Button
        {
            Name = "ReloadSourceButton", Content = "重新加载", Padding = new Thickness(14, 8),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        AutomationProperties.SetAutomationId(reload, "Previewer.Reload");
        reload.Click += (_, _) => _session.Reload();
        var connection = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        connection.Children.Add(new TextBlock
            { Text = "连接管道", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(pipe, 1);
        Grid.SetColumn(reload, 2);
        connection.Children.Add(pipe);
        connection.Children.Add(reload);

        var heading = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "XAML 源文件预览", FontSize = 22, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White
                },
                source, connection
            }
        };
        var header = new Border
            { Background = Brush.Parse("#172137"), Padding = new Thickness(24, 20), Child = heading };
        _viewport = new ContentControl
        {
            Name = "PreviewDocumentHost", HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        AutomationProperties.SetAutomationId(_viewport, "Previewer.Document");
        _windowNotice = new TextBlock
        {
            Name = "PreviewWindowNotice", TextWrapping = TextWrapping.Wrap, FontSize = 16,
            Foreground = Brush.Parse("#42516A"), Margin = new Thickness(24)
        };
        AutomationProperties.SetAutomationId(_windowNotice, "Previewer.WindowNotice");
        var surface = new Border
        {
            Background = Brushes.White, BorderBrush = Brush.Parse("#DCE3EF"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Margin = new Thickness(20),
            Child = new ScrollViewer
            {
                Content = _viewport,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            }
        };
        _status = new TextBlock
        {
            Name = "PreviewStatusText", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#42516A"),
            FontSize = 12
        };
        AutomationProperties.SetAutomationId(_status, "Previewer.Status");
        _error = new TextBlock
        {
            Name = "PreviewErrorText", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#B33845"),
            MaxHeight = 140, IsVisible = false
        };
        AutomationProperties.SetAutomationId(_error, "Previewer.Error");
        var footer = new Border
        {
            Background = Brushes.White, BorderBrush = Brush.Parse("#DCE3EF"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 12),
            Child = new StackPanel { Spacing = 6, Children = { _status, _error } }
        };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        Grid.SetRow(surface, 1);
        Grid.SetRow(footer, 2);
        layout.Children.Add(header);
        layout.Children.Add(surface);
        layout.Children.Add(footer);
        Content = layout;
        _session.PropertyChanged += OnSessionChanged;
        Opened += (_, _) => _session.Start();
        Closed += (_, _) =>
        {
            _documentWindow?.Close();
            _session.PropertyChanged -= OnSessionChanged;
            _session.Dispose();
        };
        Refresh();
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs args) => Refresh();

    private void Refresh()
    {
        if (_session.Content is Window documentWindow)
        {
            _viewport.Content = _windowNotice;
            if (!ReferenceEquals(_documentWindow, documentWindow))
            {
                _documentWindow?.Close();
                _documentWindow = documentWindow;
                documentWindow.Show();
                _windowNotice.Text = "预览内容已在独立窗口显示：" + (documentWindow.Title ?? Path.GetFileName(_session.FilePath));
                documentWindow.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_documentWindow, documentWindow))
                        _windowNotice.Text = "独立预览窗口已关闭。点击重新加载或保存源文件可重新打开预览。";
                };
            }
        }
        else
        {
            _documentWindow?.Close();
            _documentWindow = null;
            _viewport.Content = _session.Content;
        }

        _status.Text = _session.Status;
        _error.Text = _session.Error ?? _session.WatchError;
        _error.IsVisible = !string.IsNullOrEmpty(_error.Text);
    }
}
