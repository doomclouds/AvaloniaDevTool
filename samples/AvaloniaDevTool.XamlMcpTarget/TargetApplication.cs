using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using XamlMcp.Avalonia;

namespace AvaloniaDevTool.XamlMcpTarget;

public sealed class TargetApplication : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var model = new TargetModel();
            var healthy = new TextBlock { Name = "HealthyBindingTarget", FontSize = 18 };
            healthy.Bind(TextBlock.TextProperty, new Binding(nameof(TargetModel.Message)) { Source = model });
            var broken = new TextBlock { Name = "BrokenBindingTarget", Height = 24 };
            broken.Bind(TextBlock.TextProperty, new Binding("MissingValue") { Source = model });
            var counter = new TextBlock { Name = "TargetCounter", Text = "0" };
            var button = new Button { Name = "TargetIncrement", Content = "更新目标", Classes = { "backendProbe" } };
            var revision = 0;
            button.Click += (_, _) =>
            {
                counter.Text = (++revision).ToString();
                model.Message = $"开源 Agent 的绑定更新 #{revision}";
            };
            var panel = new StackPanel
            {
                Name = "BackendTargetPanel", Margin = new Thickness(24), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "XamlMcp 独立目标", FontSize = 25, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = $"PID {Environment.ProcessId} · pid:{Environment.ProcessId}" },
                    new TextBlock { Text = "仅引用 NuGet XamlMcp.Avalonia，未引用本项目 Agent。", TextWrapping = TextWrapping.Wrap },
                    healthy, broken, button, counter,
                    new TextBox { Name = "TargetInput", Text = "可读取的输入框" }
                }
            };
            for (var index = 0; index < Program.ExtraNodes; index++)
                panel.Children.Add(new TextBlock { Name = "ExtraTarget" + index, Text = $"附加节点 {index}" });
            var window = new Window
            {
                Name = "XamlMcpTargetWindow", Title = "XamlMcp 独立目标", Width = 580, Height = 500,
                Content = new ScrollViewer { Content = panel }
            };
            window.Styles.Add(new Style(selector => selector.OfType<Button>().Class("backendProbe"))
            {
                Setters = { new Setter(Button.BackgroundProperty, Brush.Parse("#6B5CE7")), new Setter(Button.ForegroundProperty, Brushes.White) }
            });
            window.Opened += (_, _) => Dispatcher.UIThread.Post(Program.WriteReady, DispatcherPriority.Background);
            desktop.MainWindow = window;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
#if DEBUG
            this.AttachXamlMcp();
#endif
        }
        base.OnFrameworkInitializationCompleted();
    }

    private sealed class TargetModel : INotifyPropertyChanged
    {
        private string _message = "来自开源 Agent 的绑定文本";
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Message
        {
            get => _message;
            set { _message = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message))); }
        }
    }
}

