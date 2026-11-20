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
/// its arguments (see <see cref="ArgumentTemplate"/>) in the active panel's folder.
/// </summary>
/// <remarks>
/// Created and used on the UI thread. The buttons work only while the active panel shows a local folder (the
/// programs get local paths). A program that cannot be started is told to the user; once started it runs on its
/// own: its output is dropped, its exit code not shown, and it is never killed (see
/// <see cref="ProgramLauncher.Start"/>).
/// </remarks>
public sealed partial class ButtonBarViewModel : ObservableObject, IDisposable
{
    private readonly WorkspaceViewModel _workspace;
    private readonly SettingsService _settings;
    private readonly IProcessRunner _processes;
    private readonly IDialogService _dialogs;
    private readonly Action<Exception> _reportFailure;
    private PanelViewModel? _watched;
    private bool _canRun;

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
        WatchActivePanel();
    }

    /// <summary>The buttons of the settings, in order; replaced (PropertyChanged) when the settings change them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasButtons))]
    public partial IReadOnlyList<UserButton> Buttons { get; private set; }

    /// <summary>Whether there is any button; without one the bar offers to set them up.</summary>
    public bool HasButtons => Buttons.Count > 0;

    /// <summary>Whether the active panel shows a folder on the local disk.</summary>
    public bool CanRun
    {
        get => _canRun;
        private set
        {
            if (!SetProperty(ref _canRun, value)) return;
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>Starts <paramref name="button"/>'s program on the active panel; does nothing while it cannot run.
    /// Returns once the program has started (or failed to); never throws.</summary>
    [RelayCommand(CanExecute = nameof(CanRunButton))]
    public async Task RunAsync(UserButton button)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (!CanRun || _workspace.ActivePanel is not { } panel) return;
        var passive = _workspace.PassivePanel;
        var context = new ArgumentTemplate.Context(
            panel.CurrentPath.Path,
            ShellCommands.IsLocal(passive) ? passive.CurrentPath.Path : "",
            panel.CursorRow is { IsParentLink: false, Entry: { } entry } ? entry.Path.Path : null,
            panel.GetOperationTargets().Select(e => e.Path.Path).ToArray());
        try
        {
            var arguments = ArgumentTemplate.Expand(button.Arguments, context);
            var spec = ProgramLauncher.Build(button.Program, arguments, context.CurrentPath,
                _workspace.HomeDirectory);
            ProgramLauncher.Start(_processes, spec);
        }
        catch (Exception e) when (ProgramLauncher.IsStartFailure(e))
        {
            await TellAsync(Loc.Format("UserCommand_StartFailed", button.Title), e.Message);
        }
        catch (Exception e)
        {
            _reportFailure(e);
        }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        if (_watched is not null) _watched.PropertyChanged -= OnPanelPropertyChanged;
        _watched = null;
    }

    private bool CanRunButton(UserButton? button) => button is not null && CanRun;

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var buttons = _settings.Current.Buttons;
        if (!buttons.SequenceEqual(Buttons)) Buttons = buttons;
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.ActivePanel) or nameof(WorkspaceViewModel.ActiveSide))
            WatchActivePanel();
    }

    private void WatchActivePanel()
    {
        var active = _workspace.IsLoaded ? _workspace.ActivePanel : null;
        if (active != _watched)
        {
            if (_watched is not null) _watched.PropertyChanged -= OnPanelPropertyChanged;
            _watched = active;
            if (active is not null) active.PropertyChanged += OnPanelPropertyChanged;
        }
        CanRun = ShellCommands.IsLocal(active);
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.CurrentPath)) CanRun = ShellCommands.IsLocal(_watched);
    }

    private async Task TellAsync(string text, string detail)
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
