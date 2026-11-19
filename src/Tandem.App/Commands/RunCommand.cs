using System.Windows.Input;

namespace Tandem.App.Commands;

/// <summary>
/// An <see cref="ICommand"/> for menus and buttons whose parameter is a command id: enabled while
/// <see cref="CommandRegistry.CanExecute"/> allows that id (so a command a later part of the app registers shows
/// disabled until then), and running it through <paramref name="execute"/>. Anything that is not a known id is
/// disabled and does nothing.
/// </summary>
public sealed class RunCommand : ICommand
{
    private readonly CommandRegistry _registry;
    private readonly Func<string, Task> _execute;

    public RunCommand(CommandRegistry registry, Func<string, Task> execute)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(execute);
        _registry = registry;
        _execute = execute;
        _registry.CanExecuteChanged += (_, _) => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => parameter is string id && _registry.CanExecute(id);

    public void Execute(object? parameter)
    {
        if (parameter is string id && _registry.CanExecute(id)) _ = _execute(id);
    }
}
