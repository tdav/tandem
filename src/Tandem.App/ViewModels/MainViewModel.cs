using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Listing;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

/// <summary>One key of the F-key bar: the key's name, what it does, and the command it runs.</summary>
public sealed record FKeyItem(string Key, string Label, string CommandId);

/// <summary>
/// The main window's model: the workspace with its two sides, the commands and key map, the volumes, and the routing
/// of keys and typed text to the active panel.
/// </summary>
/// <remarks>
/// Created and called on the UI thread, before <see cref="WorkspaceViewModel.LoadAsync()"/>; keys and text are
/// handled only once the workspace is loaded. Owns the workspace: <see cref="Dispose"/> disposes it. The services are
/// the caller's.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int DefaultPageSize = 20;

    private readonly SynchronizationContext _ui;
    private readonly IVolumeService _volumeService;
    private IReadOnlyList<VolumeInfo> _volumes = [];
    private int _volumeReadVersion;
    private int _pageSize = DefaultPageSize;
    private volatile bool _disposed;

    /// <param name="progressWindowDelay">Waits before the progress window may appear; the real delay when null.</param>
    /// <param name="quickLookDelay">Waits before an open Quick Look follows the cursor; the real delay when null.</param>
    public MainViewModel(WorkspaceViewModel workspace, AppServices services, KeyMap keyMap,
        Func<TimeSpan, CancellationToken, Task>? progressWindowDelay = null,
        Func<TimeSpan, CancellationToken, Task>? quickLookDelay = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(keyMap);
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("The main view model must be created on the UI thread.");
        Workspace = workspace;
        Services = services;
        KeyMap = keyMap;
        RunCommand = new RunCommand(Commands, ExecuteCommandAsync);
        Left = new SideViewModel(this, PanelSide.Left, workspace.Left);
        Right = new SideViewModel(this, PanelSide.Right, workspace.Right);
        FKeys =
        [
            new("F3", Loc.Get("FKey_View"), CommandIds.FileView),
            new("F4", Loc.Get("FKey_Edit"), CommandIds.FileEdit),
            new("F5", Loc.Get("FKey_Copy"), CommandIds.FileCopy),
            new("F6", Loc.Get("FKey_Move"), CommandIds.FileMove),
            new("F7", Loc.Get("FKey_NewFolder"), CommandIds.FileMkdir),
            new("F8", Loc.Get("FKey_Delete"), CommandIds.FileDelete),
        ];

        Interaction = new DialogOperationInteraction(services.Dialogs, e => services.Log.Write(e, "dialog"));
        Operations = new OperationsViewModel(services.Operations, progressWindowDelay);
        RegisterCommands();
        Files = new FileCommands(workspace, services.Providers, services.Operations, services.Dialogs, services.Trash,
            services.NativeCopy, Commands, Operations, Interaction, ReportFailure);
        Drops = new DropHandler(workspace, Files);
        Shell = new ShellCommands(workspace, services.Shell, services.QuickLook, services.Dialogs, Commands,
            services.Providers, services.Metadata, ReportFailure, quickLookDelay);
        CommandLine = new CommandLineViewModel(workspace, services.Processes, workspace.HomeDirectory);
        Commands.RaiseCanExecuteChanged();
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        _volumeService = services.Volumes;
        _volumeService.Changed += OnVolumesChanged;
        _ = ReadVolumesAsync();
    }

    public WorkspaceViewModel Workspace { get; }

    public AppServices Services { get; }

    public CommandRegistry Commands { get; } = new();

    public KeyMap KeyMap { get; }

    /// <summary>The running and waiting file operations, for the progress window and the status indicator.</summary>
    public OperationsViewModel Operations { get; }

    /// <summary>Copy, move, new folder, delete, rename and marking by mask.</summary>
    public FileCommands Files { get; }

    /// <summary>Drag and drop of files onto the panels, through the copy/move dialog.</summary>
    public DropHandler Drops { get; }

    /// <summary>Opening files, Quick Look, Reveal in Finder, the terminal, "Open With" and the properties dialog.</summary>
    public ShellCommands Shell { get; }

    /// <summary>The command line under the panels and its output pane.</summary>
    public CommandLineViewModel CommandLine { get; }

    /// <summary>Puts the operations' questions to the user, one at a time.</summary>
    public DialogOperationInteraction Interaction { get; }

    /// <summary>Runs the command whose id is the parameter; disabled while that command cannot run (including one not
    /// registered yet). For menus and buttons.</summary>
    public ICommand RunCommand { get; }

    public SideViewModel Left { get; }

    public SideViewModel Right { get; }

    /// <summary>The side with the active panel.</summary>
    public SideViewModel ActiveSide => Side(Workspace.ActiveSide);

    public SideViewModel Side(PanelSide side) => side == PanelSide.Left ? Left : Right;

    /// <summary>F3–F8 of the F-key bar.</summary>
    public IReadOnlyList<FKeyItem> FKeys { get; }

    /// <summary>The mounted volumes; read again when <see cref="IVolumeService.Changed"/> reports a change.</summary>
    public IReadOnlyList<VolumeInfo> Volumes
    {
        get => _volumes;
        private set => SetProperty(ref _volumes, value);
    }

    /// <summary>How many rows PageUp/PageDown move; the view sets it from the visible rows.</summary>
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = Math.Max(1, value);
    }

    /// <summary>Whether the active panel's folder is among the favourites.</summary>
    public bool IsCurrentFavorite =>
        Workspace.ActivePanel.CurrentPath is var path && path != default && Workspace.Favorites.Items.Contains(path.Path);

    /// <summary>A command failed with an unexpected exception (from a key, a menu or a button). The app logs it and
    /// tells the user; the app keeps running.</summary>
    public event EventHandler<Exception>? CommandFailed;

    /// <summary>The command line should get the keyboard focus.</summary>
    public event EventHandler? CommandLineFocusRequested;

    /// <summary>The favourites menu should open at the active panel.</summary>
    public event EventHandler? FavoritesMenuRequested;

    /// <summary>Something the user should be told in a message box, as localized text.</summary>
    public event EventHandler<string>? NoticeRequested;

    /// <summary>
    /// A key chord pressed while a panel (not a text editor) has the focus: cursor keys, Enter and Esc act on the
    /// active panel, Backspace edits a running quick search, anything else runs the command <see cref="KeyMap"/>
    /// binds to it. True when the key was used. A command that throws is reported through
    /// <see cref="CommandFailed"/>, not thrown. Completes synchronously unless the command itself waits.
    /// </summary>
    public async Task<bool> HandleKeyAsync(KeyChord chord)
    {
        try
        {
            if (PanelKey(chord) is { } panelTask)
            {
                await panelTask;
                return true;
            }
            return KeyMap.Find(chord) is { } id && await Commands.ExecuteAsync(id);
        }
        catch (Exception e)
        {
            ReportFailure(e);
            return true;
        }
    }

    /// <summary>Text typed while a panel has the focus: quick search in the active panel. Blank text and control
    /// characters are ignored (Space and Enter are keys with their own meaning). True when a row matched.</summary>
    public bool HandleTextInput(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl)) return false;
        return Workspace.ActivePanel.QuickSearchInput(text);
    }

    /// <summary>Runs a command for a menu or a button; a failure is reported through <see cref="CommandFailed"/>.</summary>
    public async Task ExecuteCommandAsync(string id)
    {
        try
        {
            await Commands.ExecuteAsync(id);
        }
        catch (Exception e)
        {
            ReportFailure(e);
        }
    }

    public void ToggleSidebar() => Workspace.View = Workspace.View with { Sidebar = !Workspace.View.Sidebar };

    public void ToggleVolumeBars() => Workspace.View = Workspace.View with { VolumeBars = !Workspace.View.VolumeBars };

    public void ToggleCommandLine() =>
        Workspace.View = Workspace.View with { CommandLine = !Workspace.View.CommandLine };

    public void ToggleFKeyBar() => Workspace.View = Workspace.View with { FKeyBar = !Workspace.View.FKeyBar };

    /// <summary>Chooses the language for the next start ("en", "ru", or null for the system's) and tells the user
    /// it applies after a restart.</summary>
    public void SetLanguage(string? code)
    {
        if (Workspace.Language == code) return;
        Workspace.Language = code;
        NoticeRequested?.Invoke(this, Loc.Get("Menu_LanguageRestartNotice"));
    }

    public void AddCurrentToFavorites()
    {
        var path = Workspace.ActivePanel.CurrentPath;
        if (path != default) Workspace.Favorites.Add(path.Path);
    }

    public void RemoveCurrentFromFavorites()
    {
        var path = Workspace.ActivePanel.CurrentPath;
        if (path != default) Workspace.Favorites.Remove(path.Path);
    }

    /// <summary>Opens a favourite folder (a local path) in the active panel.</summary>
    [RelayCommand]
    public Task OpenFavoriteAsync(string path) =>
        SideViewModel.ParsePath(path, VfsPath.LocalProviderId, Workspace.HomeDirectory) is { } target
            ? Workspace.ActivePanel.NavigateToAsync(target)
            : Task.CompletedTask;

    /// <summary>Opens a volume's root in the active panel.</summary>
    [RelayCommand]
    public Task OpenVolumeAsync(VolumeInfo volume) => ActiveSide.OpenVolumeAsync(volume);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _volumeService.Changed -= OnVolumesChanged;
        Workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        Interaction.Close();
        Shell.CloseQuickLook();
        CommandLine.Dispose();
        Shell.Dispose();
        Files.Dispose();
        Operations.Dispose();
        Workspace.Dispose();
    }

    /// <summary>The main window is closing: questions of running operations are answered as cancelled, so no
    /// operation waits for a dialog that will never be shown, Quick Look closes, and a command still running in
    /// the command line is killed.</summary>
    public void OnWindowClosing()
    {
        Interaction.Close();
        Shell.CloseQuickLook();
        CommandLine.Dispose();
    }

    internal void ReportFailure(Exception error) => CommandFailed?.Invoke(this, error);

    /// <summary>The cursor and panel keys, or null when the chord is not one of them.</summary>
    private Task? PanelKey(KeyChord chord)
    {
        var shift = chord.Modifiers == KeyModifiers.Shift;
        if (chord.Modifiers != KeyModifiers.None && !shift) return null;

        var panel = Workspace.ActivePanel;
        int? move = chord.Key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            Key.PageUp => -PageSize,
            Key.PageDown => PageSize,
            // Past the end on purpose: with Shift the move then marks through the first or last row.
            Key.Home => -panel.Rows.Count,
            Key.End => panel.Rows.Count,
            _ => null,
        };
        if (move is { } delta)
        {
            panel.MoveCursor(delta, shift);
            return Task.CompletedTask;
        }
        if (shift) return null;

        switch (chord.Key)
        {
            case Key.Enter:
                return panel.ActivateAsync();
            case Key.Escape when panel.QuickSearchText.Length > 0:
                panel.QuickSearchCancel();
                return Task.CompletedTask;
            case Key.Back when panel.QuickSearchText.Length > 0:
                panel.QuickSearchBackspace();
                return Task.CompletedTask;
            default:
                return null;
        }
    }

    private void RegisterCommands()
    {
        void On(string id, Action action) => Commands.Register(id, () =>
        {
            action();
            return Task.CompletedTask;
        });

        PanelTabsViewModel ActiveTabs() => ActiveSide.Tabs;
        PanelViewModel Panel() => Workspace.ActivePanel;

        On(CommandIds.PanelSwitch, Workspace.SwitchPanel);
        Commands.Register(CommandIds.PanelRefresh, () =>
        {
            _ = ReadVolumesAsync(); // free space may have changed too
            return Panel().RefreshAsync();
        });
        Commands.Register(CommandIds.PanelSwap, Workspace.SwapPanelsAsync);
        Commands.Register(CommandIds.PanelMirror, Workspace.MirrorToOtherAsync);
        Commands.Register(CommandIds.NavUp, () => Panel().GoUpAsync());
        Commands.Register(CommandIds.NavBack, () => ActiveTabs().GoBackAsync());
        Commands.Register(CommandIds.NavForward, () => ActiveTabs().GoForwardAsync());
        On(CommandIds.PathEdit, () => ActiveSide.BeginPathEdit());
        On(CommandIds.CmdlineFocus, () => CommandLineFocusRequested?.Invoke(this, EventArgs.Empty));
        On(CommandIds.SelectToggleDown, () => Panel().ToggleSelectAndMoveDown());
        On(CommandIds.SelectAll, () => Panel().SelectAll());
        On(CommandIds.SelectInvert, () => Panel().InvertSelection());
        Commands.Register(CommandIds.TabNew, () => ActiveTabs().NewTabAsync());
        On(CommandIds.TabClose, () => ActiveTabs().CloseActive());
        On(CommandIds.TabNext, () => ActiveTabs().Next());
        On(CommandIds.TabPrev, () => ActiveTabs().Previous());
        On(CommandIds.FavoritesShow, () => FavoritesMenuRequested?.Invoke(this, EventArgs.Empty));
        On(CommandIds.ViewToggleHidden, () => Workspace.ShowHidden = !Workspace.ShowHidden);
        On(CommandIds.SortByName, () => ActiveSide.SortBy(SortColumn.Name));
        On(CommandIds.SortByExtension, () => ActiveSide.SortBy(SortColumn.Extension));
        On(CommandIds.SortBySize, () => ActiveSide.SortBy(SortColumn.Size));
        On(CommandIds.SortByDate, () => ActiveSide.SortBy(SortColumn.Modified));
        On(CommandIds.SortReverse, () => ActiveSide.ReverseSort());
        Commands.RaiseCanExecuteChanged();
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkspaceViewModel.ActiveSide):
            case nameof(WorkspaceViewModel.ActivePanel):
                OnPropertyChanged(nameof(ActiveSide));
                Left.OnActiveSideChanged();
                Right.OnActiveSideChanged();
                break;
            case nameof(WorkspaceViewModel.View):
                Left.OnViewChanged();
                Right.OnViewChanged();
                break;
        }
    }

    /// <summary>Any thread. The service may raise one late event after this model is disposed, and must never be
    /// blocked: post to the UI thread and drop the event once disposed.</summary>
    private void OnVolumesChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        try
        {
            _ui.Post(_ =>
            {
                if (!_disposed) _ = ReadVolumesAsync();
            }, null);
        }
        catch (Exception)
        {
            // the UI is shutting down
        }
    }

    private async Task ReadVolumesAsync()
    {
        var version = ++_volumeReadVersion;
        IReadOnlyList<VolumeInfo> volumes;
        try
        {
            var service = _volumeService;
            volumes = await Task.Run(service.GetVolumes);
        }
        catch (Exception e)
        {
            Services.Log.Write(e, "volumes"); // keep the volumes known so far
            return;
        }
        if (_disposed || version != _volumeReadVersion) return;
        Volumes = volumes;
        Left.OnVolumesChanged();
        Right.OnVolumesChanged();
    }
}
