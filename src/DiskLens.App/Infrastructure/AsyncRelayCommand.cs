using System.Windows.Input;

namespace DiskLens.App.Infrastructure;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _executeAsync;
    private readonly Predicate<object?>? _canExecute;
    private readonly bool _allowConcurrentExecutions;
    private int _runningCount;

    public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null, bool allowConcurrentExecutions = false)
        : this(_ => executeAsync(), canExecute is null ? null : _ => canExecute(), allowConcurrentExecutions)
    {
    }

    public AsyncRelayCommand(Func<object?, Task> executeAsync, Predicate<object?>? canExecute = null, bool allowConcurrentExecutions = false)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
        _allowConcurrentExecutions = allowConcurrentExecutions;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => (_allowConcurrentExecutions || _runningCount == 0) && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter) => await ExecuteAsync(parameter).ConfigureAwait(true);

    public async Task ExecuteAsync(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _runningCount++;
        RaiseCanExecuteChanged();
        try
        {
            await _executeAsync(parameter).ConfigureAwait(true);
        }
        finally
        {
            _runningCount--;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
