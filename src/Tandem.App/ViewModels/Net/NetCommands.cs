using System.ComponentModel;
using Tandem.App.Commands;

namespace Tandem.App.ViewModels.Net;

/// <summary>
/// The Network menu: connecting to a server, disconnecting, and the SSH console. The console's file
/// (<c>NetCommands.Console.cs</c>) registers its command through <see cref="On"/> and its cleanup through
/// <see cref="OnDispose"/> from <see cref="RegisterConsole"/>; a command not built yet stays unregistered, and its
/// menu item disabled.
/// </summary>
/// <remarks>
/// Created and used on the UI thread. The availability of every command registered with <see cref="On"/> is checked
/// again when the active panel changes, or its folder or error; a command whose availability depends on more calls
/// <see cref="UpdateAvailability"/> itself. Quitting the app awaits <see cref="CloseAsync"/> (for a while at most)
/// once the user has agreed to quit.
/// </remarks>
public sealed partial class NetCommands : IDisposable
{
    private readonly WorkspaceViewModel _workspace;
    private readonly AppServices _services;
    private readonly FileCommands _files;
    private readonly CommandRegistry _commands;
    private readonly Action<Exception> _reportFailure;
    private readonly Func<Func<bool>, IDisposable> _onEscape;
    private readonly List<IDisposable> _escapeHandlers = [];
    private readonly List<string> _ids = [];
    private readonly List<Action> _cleanups = [];
    private readonly List<Func<Task>> _closes = [];
    private PanelViewModel? _watched;
    private bool[] _available = [];
    private bool _disposed;

    /// <param name="onEscape">Adds an Esc handler asked before the panel's keys (the main view model's
    /// <c>OnEscape</c>); disposing its result removes it.</param>
    public NetCommands(WorkspaceViewModel workspace, AppServices services, FileCommands files, CommandRegistry commands,
        Action<Exception> reportFailure, Func<Func<bool>, IDisposable> onEscape)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        _onEscape = onEscape ?? throw new ArgumentNullException(nameof(onEscape));

        RegisterConsole();
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        WatchPanel();
    }

    /// <summary>The console's file registers its command; called from the constructor.</summary>
    partial void RegisterConsole();

    /// <summary>True while a connection to a server is open: quitting then goes through the main view model's
    /// <c>ConfirmCloseAsync</c>, which awaits <see cref="CloseAsync"/>. Never true before connections exist.</summary>
    public bool HasOpenConnections => false;

    /// <summary>Closes the network connections before the app quits: runs every close added with
    /// <see cref="OnClose"/> at once and completes when all have (a failure of one faults the task once all are
    /// done). Nothing to do while none was added.</summary>
    public Task CloseAsync() => Task.WhenAll(_closes.ToArray().Select(RunClose));

    /// <summary>Adds work <see cref="CloseAsync"/> runs and waits for (closing connections, ending console
    /// sessions).</summary>
    public void OnClose(Func<Task> close)
    {
        ArgumentNullException.ThrowIfNull(close);
        _closes.Add(close);
    }

    /// <summary>Lets <paramref name="handler"/> take Esc before the panel does (while a connection is being made, so
    /// Esc cancels it): true takes the key. Disposing the result removes the handler; <see cref="Dispose"/> removes
    /// every one still registered.</summary>
    public IDisposable OnEscapeKey(Func<bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var registration = _onEscape(handler);
        _escapeHandlers.Add(registration);
        return new Registration(() =>
        {
            _escapeHandlers.Remove(registration);
            registration.Dispose();
        });
    }

    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? _remove = remove;

        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }

    /// <summary>Registers the command <paramref name="id"/> and includes it in the availability checks.</summary>
    private void On(string id, Func<Task> run, Func<bool> canRun)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(canRun);
        if (!_ids.Contains(id)) _ids.Add(id);
        _commands.Register(id, run, canRun);
    }

    /// <summary>Adds work <see cref="Dispose"/> does: the cleanups run in the reverse order of their adding.</summary>
    private void OnDispose(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        _cleanups.Add(cleanup);
    }

    /// <summary>The active panel; null until the workspace is loaded.</summary>
    private PanelViewModel? Active => _workspace.IsLoaded ? _workspace.ActivePanel : null;

    /// <summary>Runs the cleanups (last added first; one that throws is reported and the others still run) and stops
    /// watching the panel and its Esc handlers. Closes no connection: that is <see cref="CloseAsync"/>.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        if (_watched is not null) _watched.PropertyChanged -= OnPanelPropertyChanged;
        _watched = null;
        foreach (var registration in _escapeHandlers.ToArray()) registration.Dispose();
        _escapeHandlers.Clear();
        for (var i = _cleanups.Count - 1; i >= 0; i--)
        {
            try
            {
                _cleanups[i]();
            }
            catch (Exception e)
            {
                _reportFailure(e);
            }
        }
        _cleanups.Clear();
    }

    private static async Task RunClose(Func<Task> close) => await close();

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.ActivePanel)) WatchPanel();
    }

    private void WatchPanel()
    {
        if (_watched is not null) _watched.PropertyChanged -= OnPanelPropertyChanged;
        _watched = Active;
        if (_watched is not null) _watched.PropertyChanged += OnPanelPropertyChanged;
        UpdateAvailability();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PanelViewModel.CurrentPath) or nameof(PanelViewModel.Error)) UpdateAvailability();
    }

    /// <summary>Checks every registered command's availability; raises the registry's change only when one
    /// changed.</summary>
    private void UpdateAvailability()
    {
        var available = _ids.Select(_commands.CanExecute).ToArray();
        if (available.SequenceEqual(_available)) return;
        _available = available;
        _commands.RaiseCanExecuteChanged();
    }
}
