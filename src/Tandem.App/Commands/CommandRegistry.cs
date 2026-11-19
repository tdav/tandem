namespace Tandem.App.Commands;

/// <summary>
/// What each command does: a handler and an optional "can it run now" check per command id. Used on the UI thread
/// only. While a command's handler is running, running the same command again is refused, so a held-down key does
/// not start it twice.
/// </summary>
public sealed class CommandRegistry
{
    private readonly Dictionary<string, (Func<Task> Execute, Func<bool>? CanExecute)> _commands = [];
    private readonly HashSet<string> _running = [];

    /// <summary>Raised by <see cref="RaiseCanExecuteChanged"/>, for menus to re-check <see cref="CanExecute"/>.</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>Registers a command; registering an id again replaces its handler and check.</summary>
    public void Register(string id, Func<Task> execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(execute);
        _commands[id] = (execute, canExecute);
    }

    /// <summary>Whether the command exists and its check (if any) allows it.</summary>
    public bool CanExecute(string id) =>
        _commands.TryGetValue(id, out var command) && (command.CanExecute?.Invoke() ?? true);

    /// <summary>
    /// Runs the command and waits for its handler. Returns false without running it if the command does not exist,
    /// its check refuses, or the same command is still running. An exception from the handler propagates.
    /// </summary>
    public async Task<bool> ExecuteAsync(string id)
    {
        if (!CanExecute(id) || !_running.Add(id))
            return false;

        try
        {
            await _commands[id].Execute();
            return true;
        }
        finally
        {
            _running.Remove(id);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
