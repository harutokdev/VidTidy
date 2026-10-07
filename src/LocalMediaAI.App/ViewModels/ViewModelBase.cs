using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LocalMediaAI.App.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly bool _allowConcurrentExecutions;
    private int _runningCount;
    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null, bool allowConcurrentExecutions = false)
    {
        _execute = execute;
        _canExecute = canExecute;
        _allowConcurrentExecutions = allowConcurrentExecutions;
    }
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) =>
        (_allowConcurrentExecutions || Volatile.Read(ref _runningCount) == 0) && (_canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        Interlocked.Increment(ref _runningCount);
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await _execute(); }
        finally
        {
            Interlocked.Decrement(ref _runningCount);
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
