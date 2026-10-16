using System.Windows.Input;

namespace Syno.TinyTorrent.Shared;

internal sealed class RelayCommand(Func<Task> execute, Func<bool> enabled) : ICommand
{
    internal RelayCommand(Action execute, Func<bool> enabled)
        : this(
            () =>
            {
                execute();
                return Task.CompletedTask;
            },
            enabled
        ) { }

    public bool CanExecute(object? parameter) => enabled();

    public async void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            await execute();
    }

    public event EventHandler? CanExecuteChanged;

    internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
