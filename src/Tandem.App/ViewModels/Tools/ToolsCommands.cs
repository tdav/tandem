using System.ComponentModel;
using Tandem.App.Commands;
using Tandem.Core.Operations;

namespace Tandem.App.ViewModels.Tools;

/// <summary>
/// The Tools menu: multi-rename and its undo, folder comparison and synchronization, file comparison, checksums and
/// folder sizes. Each tool lives in a file of its own (<c>ToolsCommands.&lt;Tool&gt;.cs</c>) that registers its
/// commands through <see cref="On"/> and its cleanup through <see cref="OnDispose"/>, from the partial method this
/// file calls in the constructor; a tool not built yet has no commands, and its menu items stay disabled.
/// </summary>
/// <remarks>
/// Created and used on the UI thread. The availability of every command registered with <see cref="On"/> is checked
/// again when the active or passive panel changes, or its cursor, marks, folder, error or rows; a tool whose
/// availability depends on more calls <see cref="UpdateAvailability"/> itself.
/// <see cref="CommandRegistry.RaiseCanExecuteChanged"/> is raised only when an availability changed.
/// </remarks>
public sealed partial class ToolsCommands : IDisposable
{
    private readonly WorkspaceViewModel _workspace;
    private readonly AppServices _services;
    private readonly FileCommands _files;
    private readonly IOperationInteraction _interaction;
    private readonly CommandRegistry _commands;
    private readonly Action<Exception> _reportFailure;
    private readonly List<string> _ids = [];
    private readonly List<Action> _cleanups = [];
    private readonly List<Task> _pending = [];
    private readonly List<PanelViewModel> _watched = [];
    private bool[] _available = [];
    private bool _disposed;

    public ToolsCommands(WorkspaceViewModel workspace, AppServices services, FileCommands files,
        IOperationInteraction interaction, CommandRegistry commands, Action<Exception> reportFailure)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));

        RegisterRename();
        RegisterChecksums();
        RegisterFolderSizes();
        RegisterCompareDirs();
        RegisterCompareFiles();
        RegisterSync();
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        WatchPanels();
    }

    // Each tool's file registers its commands; called from the constructor.
    partial void RegisterRename();
    partial void RegisterChecksums();
    partial void RegisterFolderSizes();
    partial void RegisterCompareDirs();
    partial void RegisterCompareFiles();
    partial void RegisterSync();

    /// <summary>The active panel; null until the workspace is loaded.</summary>
    private PanelViewModel? Active => _workspace.IsLoaded ? _workspace.ActivePanel : null;

    /// <summary>The passive panel; null until the workspace is loaded.</summary>
    private PanelViewModel? Passive => _workspace.IsLoaded ? _workspace.PassivePanel : null;

    /// <summary>Registers the command <paramref name="id"/> and includes it in the availability checks. While it
    /// runs, <see cref="WhenIdleAsync"/> waits for it.</summary>
    private void On(string id, Func<Task> run, Func<bool> canRun)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(canRun);
        if (!_ids.Contains(id)) _ids.Add(id);
        _commands.Register(id, () => Tracked(run()), canRun);
    }

    /// <summary>Adds work <see cref="Dispose"/> does: the cleanups run in the reverse order of their adding.</summary>
    private void OnDispose(Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        _cleanups.Add(cleanup);
    }

    /// <summary>Completes when no command of this class is running (its windows answered and its operations handed
    /// to the queue). Exceptions are not rethrown.</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            _pending.RemoveAll(t => t.IsCompleted);
            if (_pending.Count == 0) return;
            try
            {
                await Task.WhenAll(_pending.ToArray());
            }
            catch (Exception)
            {
                // the command's own caller hears about it
            }
        }
    }

    /// <summary>Runs the cleanups (last added first; one that throws is reported and the others still run) and stops
    /// watching the panels.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        foreach (var panel in _watched) panel.PropertyChanged -= OnPanelPropertyChanged;
        _watched.Clear();
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

    private Task Tracked(Task task)
    {
        _pending.RemoveAll(t => t.IsCompleted);
        _pending.Add(task);
        return task;
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.ActivePanel) or nameof(WorkspaceViewModel.PassivePanel))
            WatchPanels();
    }

    private void WatchPanels()
    {
        foreach (var panel in _watched) panel.PropertyChanged -= OnPanelPropertyChanged;
        _watched.Clear();
        if (_workspace.IsLoaded)
        {
            foreach (var panel in new[] { _workspace.ActivePanel, _workspace.PassivePanel })
            {
                panel.PropertyChanged += OnPanelPropertyChanged;
                _watched.Add(panel);
            }
        }
        UpdateAvailability();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PanelViewModel.CursorIndex) or nameof(PanelViewModel.SelectedCount)
            or nameof(PanelViewModel.CurrentPath) or nameof(PanelViewModel.Error) or nameof(PanelViewModel.TotalCount)
            or nameof(PanelViewModel.HasFolders) or nameof(PanelViewModel.IsSizing))
            UpdateAvailability();
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
