using System.Windows.Input;

namespace Tandem.App.Commands;

/// <summary>
/// An <see cref="ICommand"/> for menus and buttons whose parameter is a command id: enabled while
/// <see cref="CommandRegistry.CanExecute"/> allows that id (so a command a later part of the app registers shows
/// disabled until then), and running it through <paramref name="execute"/>. Anything that is not a known id is
/// disabled and does nothing.
/// </summary>
/// <remarks>
/// Made with <c>alwaysEnabled</c>, it reports every command id as enabled and never raises
/// <see cref="CanExecuteChanged"/> (the F-key bar: its buttons do not flicker as the cursor moves); a click on an
/// unavailable command reaches <paramref name="execute"/>, which does nothing for it
/// (<see cref="CommandRegistry.ExecuteAsync"/> checks again).
/// </remarks>
public sealed class RunCommand : ICommand
{
    private readonly CommandRegistry _registry;
    private readonly Func<string, Task> _execute;
    private readonly bool alwaysEnabled;

    public RunCommand(CommandRegistry registry, Func<string, Task> execute, bool alwaysEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(execute);
        _registry = registry;
        _execute = execute;
        this.alwaysEnabled = alwaysEnabled;
        if (!alwaysEnabled)
            _registry.CanExecuteChanged += (_, _) => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        parameter is string id && (alwaysEnabled || _registry.CanExecute(id));

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter)) _ = _execute((string)parameter!);
    }
}
