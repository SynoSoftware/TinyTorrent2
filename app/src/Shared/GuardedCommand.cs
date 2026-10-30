using System.Windows.Input;

namespace Syno.TinyTorrent.Shared;

internal sealed class GuardedCommand(ICommand command, Func<bool> enabled) : ICommand
{
    private event EventHandler? Changed;

    public bool CanExecute(object? parameter) => enabled() && command.CanExecute(parameter);

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
            command.Execute(parameter);
    }

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            Changed += value;
            command.CanExecuteChanged += value;
        }
        remove
        {
            Changed -= value;
            command.CanExecuteChanged -= value;
        }
    }

    internal void Refresh() => Changed?.Invoke(this, EventArgs.Empty);
}
