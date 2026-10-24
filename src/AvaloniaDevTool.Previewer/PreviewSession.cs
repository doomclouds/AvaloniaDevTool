using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AvaloniaDevTool.Previewer;

public sealed class PreviewSession : INotifyPropertyChanged, IDisposable
{
    private readonly PreviewDocumentLoader _loader;
    private readonly bool _watch;
    private readonly TimeSpan? _debounce;
    private PreviewFileWatcher? _watcher;
    private bool _started, _disposed;

    public PreviewSession(PreviewDocumentLoader loader, bool watch = true, TimeSpan? debounce = null)
    {
        _loader = loader;
        _watch = watch;
        _debounce = debounce;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string FilePath => _loader.FilePath;
    public bool WatchEnabled => _watcher is not null;
    public Control? Content { get; private set; }
    public string Status { get; private set; } = "等待加载源文件。";
    public string? Error { get; private set; }
    public string? WatchError { get; private set; }
    public long SuccessfulLoads { get; private set; }
    public DateTimeOffset? LastSuccessfulLoad { get; private set; }

    public void Start()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return;
        _started = true;
        if (_watch)
        {
            try { _watcher = new PreviewFileWatcher(_loader.FilePath, () => Reload(), _debounce); }
            catch (Exception exception) when (exception is PreviewDocumentException or IOException or UnauthorizedAccessException)
            {
                WatchError = "自动监视未启动：" + exception.Message;
            }
        }
        Reload();
    }

    public bool Reload()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            if (_watcher?.LastError is { } watcherError)
                WatchError = "文件监视报告异常：" + watcherError;
            var candidate = _loader.Load();
            Content = candidate;
            Error = null;
            SuccessfulLoads++;
            LastSuccessfulLoad = DateTimeOffset.UtcNow;
            Status = $"已加载 {Path.GetFileName(FilePath)} · 第 {SuccessfulLoads} 次 · {(WatchEnabled ? "自动监视开启" : "手动重载")}";
            RaiseState();
            return true;
        }
        catch (PreviewDocumentException exception)
        {
            var message = $"[{exception.Code}] {exception.Message}";
            Error = message.Length <= 4096 ? message : message[..4096];
            Status = Content is null ? "加载失败，等待修正源文件。" : "加载失败，保留上次成功预览。";
            RaiseState();
            return false;
        }
    }

    private void RaiseState()
    {
        foreach (var name in new[] { nameof(Content), nameof(Status), nameof(Error), nameof(WatchError), nameof(SuccessfulLoads), nameof(LastSuccessfulLoad), nameof(WatchEnabled) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;
        _disposed = true;
        _watcher?.Dispose();
        _watcher = null;
        Content = null;
        _loader.Dispose();
    }
}

