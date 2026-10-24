using Avalonia.Threading;

namespace AvaloniaDevTool.Previewer;

public sealed class PreviewFileWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _timer;
    private readonly Action _reload;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private int _generation;
    private bool _disposed;

    public PreviewFileWatcher(string absolutePath, Action reload, TimeSpan? debounce = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        absolutePath = PreviewOptions.AbsoluteFilePath(absolutePath, "--xaml");
        _reload = reload;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
        if (_debounce < TimeSpan.FromMilliseconds(50) || _debounce > TimeSpan.FromSeconds(2))
            throw new ArgumentOutOfRangeException(nameof(debounce));
        var directory = Path.GetDirectoryName(absolutePath)!;
        if (!Directory.Exists(directory))
            throw new PreviewDocumentException("directory_not_found", $"XAML directory was not found: {directory}");
        _timer = new Timer(OnElapsed);
        _watcher = new FileSystemWatcher(directory, Path.GetFileName(absolutePath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    public string? LastError { get; private set; }

    private void OnChanged(object sender, FileSystemEventArgs args) => Schedule();
    private void OnRenamed(object sender, RenamedEventArgs args) => Schedule();
    private void OnError(object sender, ErrorEventArgs args)
    {
        lock (_gate)
            LastError = args.GetException().Message;
        Schedule();
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _generation++;
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnElapsed(object? state)
    {
        int generation;
        lock (_gate)
        {
            if (_disposed)
                return;
            generation = _generation;
        }
        Dispatcher.UIThread.Post(() =>
        {
            lock (_gate)
                if (_disposed || generation != _generation)
                    return;
            _reload();
        });
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _generation++;
        }
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Deleted -= OnChanged;
        _watcher.Renamed -= OnRenamed;
        _watcher.Error -= OnError;
        _watcher.Dispose();
        _timer.Dispose();
    }
}

