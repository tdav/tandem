using Tandem.Core.Vfs.Local;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Platform;
using Tandem.Core.Settings;

namespace Tandem.App.ViewModels;

/// <summary>
/// The button bar at the top of the main window: the user's commands from the settings, each a program started with
/// its arguments (see <see cref="ArgumentTemplate"/>) in the active panel's local folder or the user's home.
/// </summary>
/// <remarks>
/// Created and used on the UI thread. Each button is available when its arguments can be expanded using the
/// two panels. A program that cannot be started (or an application that
/// <c>open</c> cannot launch) is told to the user; once started it runs on its own: its output is dropped, its exit
/// code not shown, and it is never killed (see <see cref="ProgramLauncher.Start"/>).
/// </remarks>
public sealed partial class ButtonBarViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceViewModel _workspace;
    private readonly SettingsService _settings;
    private readonly IProcessRunner _processes;
    private readonly IDialogService _dialogs;
    private readonly Action<Exception> _reportFailure;
    private PanelViewModel? _active;
    private PanelViewModel? _passive;
    private bool _canRun;
    private bool _disposed;

    public ButtonBarViewModel(WorkspaceViewModel workspace, SettingsService settings, IProcessRunner processes,
        IDialogService dialogs, Action<Exception> reportFailure)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        Buttons = settings.Current.Buttons;
        settings.Changed += OnSettingsChanged;
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        WatchPanels();
    }

    /// <summary>The buttons of the settings, in order; replaced (PropertyChanged) when the settings change them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasButtons))]
    public partial IReadOnlyList<UserButton> Buttons { get; private set; }

    /// <summary>Whether the button bar has any configured buttons.</summary>
    public bool HasButtons => Buttons.Count > 0;

    /// <summary>Whether any configured button can run.</summary>
    public bool CanRun
    {
        get => _canRun;
        private set => SetProperty(ref _canRun, value);
    }

    /// <summary>Whether the panels supply the local paths the button needs.</summary>
    public bool IsAvailable(UserButton button)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (_disposed || !_workspace.IsLoaded) return false;
        var placeholders = ArgumentTemplate.PlaceholdersOf(button.Arguments);
        return (ShellCommands.IsLocal(_workspace.ActivePanel) ||
                !(placeholders.Contains('p') || placeholders.Contains('f') || placeholders.Contains('s'))) &&
            (ShellCommands.IsLocal(_workspace.PassivePanel) || !placeholders.Contains('t'));
    }

    /// <summary>Starts <paramref name="button"/>'s program on the active panel; does nothing while it cannot run.
    /// Returns once the program has started (or failed to); never throws.</summary>
    [RelayCommand(CanExecute = nameof(CanRunButton))]
    public async Task RunAsync(UserButton button)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (!IsAvailable(button)) return;
        var panel = _workspace.ActivePanel;
        var passive = _workspace.PassivePanel;
        var local = ShellCommands.IsLocal(panel);
        var entry = panel.CursorRow?.Entry;
        var context = new ArgumentTemplate.Context(
            local ? panel.CurrentPath.ToOsPath() : _workspace.HomeDirectory,
            ShellCommands.IsLocal(passive) ? passive.CurrentPath.ToOsPath() : null,
            local ? entry?.Path.ToOsPath() : null,
            local ? panel.GetOperationTargets().Select(e => e.Path.ToOsPath()).ToArray() : [])
        {
            CursorName = entry?.Name,
        };
        var failureText = Loc.Format("UserCommand_StartFailed", button.Title);
        Task<string?> verdict;
        try
        {
            var arguments = ArgumentTemplate.Expand(button.Arguments, context);
            var spec = ProgramLauncher.Build(button.Program, arguments, context.CurrentPath,
                _workspace.HomeDirectory);
            verdict = ProgramLauncher.Start(_processes, spec);
        }
        catch (Exception e) when (ProgramLauncher.IsStartFailure(e))
        {
            await TellAsync(failureText, e.Message);
            return;
        }
        catch (Exception e)
        {
            _reportFailure(e);
            return;
        }
        _ = ObserveAsync(verdict, failureText);
    }

    public void Dispose()
    {
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        WatchPanels();
    }

    private bool CanRunButton(UserButton? button) => button is not null && IsAvailable(button);

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var buttons = _settings.Current.Buttons;
        if (buttons.SequenceEqual(Buttons)) return;
        Buttons = buttons;
        UpdateAvailability();
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.ActivePanel) or nameof(WorkspaceViewModel.PassivePanel))
            WatchPanels();
    }

    private void WatchPanels()
    {
        if (_active is not null) _active.PropertyChanged -= OnPanelPropertyChanged;
        if (_passive is not null) _passive.PropertyChanged -= OnPanelPropertyChanged;
        var loaded = !_disposed && _workspace.IsLoaded;
        _active = loaded ? _workspace.ActivePanel : null;
        _passive = loaded ? _workspace.PassivePanel : null;
        if (_active is not null) _active.PropertyChanged += OnPanelPropertyChanged;
        if (_passive is not null) _passive.PropertyChanged += OnPanelPropertyChanged;
        UpdateAvailability();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PanelViewModel.CurrentPath) or nameof(PanelViewModel.CursorRow))
            UpdateAvailability();
    }

    private void UpdateAvailability()
    {
        CanRun = Buttons.Any(IsAvailable);
        RunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>An application that the launcher could not start is told, with the launcher's words.</summary>
    private async Task ObserveAsync(Task<string?> verdict, string failureText)
    {
        if (await verdict is { } output && !_disposed) await TellAsync(failureText, output.Length > 0 ? output : null);
    }

    private async Task TellAsync(string text, string? detail)
    {
        try
        {
            await _dialogs.ShowMessageAsync(text, detail);
        }
        catch (Exception failure)
        {
            _reportFailure(failure);
        }
    }
}
