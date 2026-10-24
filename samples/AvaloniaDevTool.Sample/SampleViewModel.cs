using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace AvaloniaDevTool.Sample;

public sealed partial class SampleViewModel : INotifyPropertyChanged
{
    private readonly bool _isDesignPreview = Design.IsDesignMode;
    private string _remoteEndpoint = "未开启远程监听；可使用 --remote 启动回环 WebSocket。";
    private bool _remoteReady;
    public string RemoteEndpoint { get => _remoteEndpoint; set => Set(ref _remoteEndpoint, value); }
    public bool RemoteReady { get => _remoteReady; set => Set(ref _remoteReady, value); }
    private string _userName = "小陌";
    private double _progress = 64;
    private bool _isPreviewEnabled = true;
    private string _selectedMode = "标准模式";
    private int _sourceRevision = 1;
    private int _dynamicCount;
    private int _validatedCount = 42;
    private string _status = "先打开检查器并连接，再开启点击拾取，选择本窗口中的控件。";
#if DEBUG
    private string _agentStatus = "Debug · 正在启动诊断 Agent";
#else
    private string _agentStatus = "Release · 诊断 Agent 未启用";
#endif

    public event PropertyChangedEventHandler? PropertyChanged;

    public string PipeName => _isDesignPreview ? "请使用预览宿主显示的连接管道" : Program.PipeName;
    internal bool IsDesignPreview => _isDesignPreview;
    public string ProcessLabel => $"PID {Environment.ProcessId}";
    public string AgentStatus
    {
        get => _isDesignPreview ? "设计预览 · 诊断由宿主提供" : _agentStatus;
        set => Set(ref _agentStatus, value);
    }
    public IReadOnlyList<string> Modes { get; } = ["标准模式", "紧凑模式", "演示模式"];

    public string UserName
    {
        get => _userName;
        set
        {
            if (Set(ref _userName, value))
                OnPropertyChanged(nameof(Greeting));
        }
    }

    public string Greeting => $"你好，{UserName}。这个文本由 UserName 绑定生成。";

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    public bool IsPreviewEnabled
    {
        get => _isPreviewEnabled;
        set => Set(ref _isPreviewEnabled, value);
    }

    public string SelectedMode
    {
        get => _selectedMode;
        set => Set(ref _selectedMode, value);
    }

    public string BoundMessage => $"绑定源第 {_sourceRevision} 次更新 · {DateTime.Now:HH:mm:ss}";
    public int ValidatedCount
    {
        get => _validatedCount;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "The value must be non-negative.");
            Set(ref _validatedCount, value);
        }
    }
    public int DynamicCount
    {
        get => _dynamicCount;
        set => Set(ref _dynamicCount, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public void UpdateBindingSource()
    {
        _sourceRevision++;
        OnPropertyChanged(nameof(BoundMessage));
        Status = $"绑定源已更新到第 {_sourceRevision} 次。可观察检查器中的 Text 属性和来源。";
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

