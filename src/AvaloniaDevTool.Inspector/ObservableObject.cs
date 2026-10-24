using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace AvaloniaDevTool.Inspector;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AsyncCommand(Func<Task> execute, Action<Exception> onError, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
    public void Execute(object? parameter) => _ = ExecuteAsync();

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private async Task ExecuteAsync()
    {
        if (!CanExecute(null))
            return;
        _running = true;
        Refresh();
        try { await execute(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { onError(exception); }
        finally { _running = false; Refresh(); }
    }
}

