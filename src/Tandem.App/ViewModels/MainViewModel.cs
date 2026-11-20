using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels.Search;
using Tandem.App.ViewModels.Settings;
using Tandem.App.ViewModels.Viewing;
using Tandem.Core.Listing;
using Tandem.Core.Platform;
using Tandem.Core.Search;
using Tandem.Core.Settings;
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
    private bool[] _tabAvailable = [];
    private SearchViewModel? _search;
    private volatile bool _disposed;
    private KeyMap _keyMap;
    private readonly (string CommandId, string Label)[] _fKeyLabels;
    private IReadOnlyList<FKeyItem> _fKeys;
    private FileColors _fileColors;
    private IReadOnlyList<FileColorRule> _fileColorRules;
    private ThemeChoice _theme;

    /// <param name="progressWindowDelay">Waits before the progress window may appear; the real delay when null.</param>
    /// <param name="quickLookDelay">Waits before an open Quick Look or quick-view panel follows the cursor; the real
    /// delay when null.</param>
    public MainViewModel(WorkspaceViewModel workspace, AppServices services, SettingsService settings,
        Func<TimeSpan, CancellationToken, Task>? progressWindowDelay = null,
        Func<TimeSpan, CancellationToken, Task>? quickLookDelay = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        _ui = SynchronizationContext.Current
              ?? throw new InvalidOperationException("The main view model must be created on the UI thread.");
        Workspace = workspace;
        Services = services;
        Settings = settings;
        _keyMap = KeyMap.Default.WithOverrides(settings.Current.KeyBindings);
        _fileColorRules = settings.Current.FileColors;
        _fileColors = new FileColors(_fileColorRules);
        _theme = settings.Current.Theme;
        SearchResults = services.Providers.TryGet(SearchResultsProvider.ProviderId, out var search) &&
                        search is SearchResultsProvider results
            ? results
            : throw new InvalidOperationException("The search results provider is not registered.");
        QuickView = new QuickViewViewModel(workspace, services.Providers, services.Temp, quickLookDelay,
            ReportFailure);
        RunCommand = new RunCommand(Commands, ExecuteCommandAsync);
        Left = new SideViewModel(this, PanelSide.Left, workspace.Left);
        Right = new SideViewModel(this, PanelSide.Right, workspace.Right);
        Left.OnSettingsChanged(settings.Current, fileColorsChanged: false);
        Right.OnSettingsChanged(settings.Current, fileColorsChanged: false);
        _fKeyLabels =
        [
            (CommandIds.FileView, Loc.Get("FKey_View")),
            (CommandIds.FileEdit, Loc.Get("FKey_Edit")),
            (CommandIds.FileCopy, Loc.Get("FKey_Copy")),
            (CommandIds.FileMove, Loc.Get("FKey_Move")),
            (CommandIds.FileMkdir, Loc.Get("FKey_NewFolder")),
            (CommandIds.FileDelete, Loc.Get("FKey_Delete")),
        ];
        _fKeys = BuildFKeys();

        Interaction = new DialogOperationInteraction(services.Dialogs, e => services.Log.Write(e, "dialog"));
        Operations = new OperationsViewModel(services.Operations, progressWindowDelay);
        RegisterCommands();
        Files = new FileCommands(workspace, services.Providers, services.Operations, services.Dialogs, services.Trash,
            services.NativeCopy, Commands, Operations, Interaction, ReportFailure);
        Drops = new DropHandler(workspace, Files);
        Shell = new ShellCommands(workspace, services.Shell, services.QuickLook, services.Dialogs, services.Windows,
            Commands, services.Providers, services.Metadata, services.Temp, ReportFailure, quickLookDelay,
            services.NativeCopy, settings, services.Processes);
        ButtonBar = new ButtonBarViewModel(workspace, settings, services.Processes, services.Dialogs, ReportFailure);
        CommandLine = new CommandLineViewModel(workspace, services.Processes, workspace.HomeDirectory);
        Commands.RaiseCanExecuteChanged();
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        QuickView.PropertyChanged += OnQuickViewPropertyChanged;
        workspace.Left.StateChanged += OnTabsStateChanged;
        workspace.Right.StateChanged += OnTabsStateChanged;
        _volumeService = services.Volumes;
        _volumeService.Changed += OnVolumesChanged;
        services.Operations.Changed += OnOperationsChanged;
        settings.Changed += OnSettingsChanged;
        settings.SaveFailed += OnSettingsSaveFailed;
        _ = ReadVolumesAsync();
    }

    public WorkspaceViewModel Workspace { get; }

    public AppServices Services { get; }

    /// <summary>The search result sets, each a folder a panel can show (registered by
    /// <see cref="Composition.Build"/>).</summary>
    public SearchResultsProvider SearchResults { get; }

    public CommandRegistry Commands { get; } = new();

    /// <summary>The user's settings; a change to them takes effect at once.</summary>
    public SettingsService Settings { get; }

    /// <summary>The default key map with the user's overrides from <see cref="Settings"/>; replaced (and
    /// PropertyChanged raised) when a settings change binds the chords differently.</summary>
    public KeyMap KeyMap
    {
        get => _keyMap;
        private set => SetProperty(ref _keyMap, value);
    }

    /// <summary>The colour rules for file rows from <see cref="Settings"/>; replaced (and PropertyChanged raised, on
    /// this and on both sides) when the rules change.</summary>
    public FileColors FileColors
    {
        get => _fileColors;
        private set => SetProperty(ref _fileColors, value);
    }

    /// <summary>The colour scheme chosen in <see cref="Settings"/>; the window applies it to the application.</summary>
    public ThemeChoice Theme
    {
        get => _theme;
        private set => SetProperty(ref _theme, value);
    }

    /// <summary>View → Theme: keeps <paramref name="theme"/> in the settings, which applies it at once.</summary>
    public void SetTheme(ThemeChoice theme)
    {
        if (Settings.Current.Theme == theme) return;
        Settings.Update(Settings.Current with { Theme = theme });
    }

    /// <summary>The running and waiting file operations, for the progress window and the status indicator.</summary>
    public OperationsViewModel Operations { get; }

    /// <summary>Copy, move, new folder, delete, rename and marking by mask.</summary>
    public FileCommands Files { get; }

    /// <summary>Drag and drop of files onto the panels, through the copy/move dialog.</summary>
    public DropHandler Drops { get; }

    /// <summary>Opening files, Quick Look, Reveal in Finder, the terminal, "Open With" and the properties dialog.</summary>
    public ShellCommands Shell { get; }

    /// <summary>The user's commands at the top of the window.</summary>
    public ButtonBarViewModel ButtonBar { get; }

    /// <summary>The quick-view panel (Ctrl+Q): the passive side shows the file under the cursor.</summary>
    public QuickViewViewModel QuickView { get; }

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

    /// <summary>The F-key bar: view, edit, copy, move, new folder, delete, each with its command's first chord in
    /// <see cref="KeyMap"/> as the Mac shows it (F3–F8 by default; empty when unbound). Replaced when the key map changes.</summary>
    public IReadOnlyList<FKeyItem> FKeys
    {
        get => _fKeys;
        private set => SetProperty(ref _fKeys, value);
    }

    private FKeyItem[] BuildFKeys() => _fKeyLabels
        .Select(k => new FKeyItem(KeyMap.ChordsFor(k.CommandId) is [var chord, ..] ? KeyChordText.Format(chord) : "", k.Label,
            k.CommandId))
        .ToArray();

    /// <summary>The mounted volumes; read again when <see cref="IVolumeService.Changed"/> reports a change, on Cmd+R,
    /// and when a file operation ends (free space).</summary>
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

    public void ToggleButtonBar() => Workspace.View = Workspace.View with { ButtonBar = !Workspace.View.ButtonBar };

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

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var settings = Settings.Current;
        Theme = settings.Theme;
        var rules = settings.FileColors;
        var fileColorsChanged = !rules.SequenceEqual(_fileColorRules);
        if (fileColorsChanged)
        {
            _fileColorRules = rules;
            FileColors = new FileColors(rules);
        }
        Left.OnSettingsChanged(settings, fileColorsChanged);
        Right.OnSettingsChanged(settings, fileColorsChanged);

        var keyMap = KeyMap.Default.WithOverrides(settings.KeyBindings);
        if (keyMap.Bindings.SequenceEqual(KeyMap.Bindings)) return;
        KeyMap = keyMap;
        FKeys = BuildFKeys();
    }

    private void OnSettingsSaveFailed(object? sender, Exception failure) =>
        NoticeRequested?.Invoke(this, Loc.Format("Settings_SaveFailed", failure.Message));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _volumeService.Changed -= OnVolumesChanged;
        Services.Operations.Changed -= OnOperationsChanged;
        Settings.Changed -= OnSettingsChanged;
        Settings.SaveFailed -= OnSettingsSaveFailed;
        Workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        QuickView.PropertyChanged -= OnQuickViewPropertyChanged;
        Workspace.Left.StateChanged -= OnTabsStateChanged;
        Workspace.Right.StateChanged -= OnTabsStateChanged;
        Interaction.Close();
        Shell.CloseQuickLook();
        _search?.Dispose();
        QuickView.Dispose();
        CommandLine.Dispose();
        ButtonBar.Dispose();
        Shell.Dispose();
        Files.Dispose();
        Operations.Dispose();
        Workspace.Dispose();
    }

    /// <summary>How long quitting waits for stopped operations to clean up (remove their part files) before it
    /// goes ahead anyway.</summary>
    public static readonly TimeSpan StopOperationsTimeout = TimeSpan.FromSeconds(10);

    /// <summary>True while file operations are running or waiting in the queue.</summary>
    public bool HasOperations => Services.Operations.Operations.Count > 0;

    /// <summary>True while an editor (or another window of <see cref="AppServices.Windows"/>) is open.</summary>
    public bool HasOpenWindows => Services.Windows.HasOpenWindows;

    /// <summary>The window (or the app) is asked to close. First every window of <see cref="AppServices.Windows"/> is
    /// closed, an editor with unsaved changes asking: if the user keeps one open, false. Then, with no file operation
    /// running or waiting, true. Otherwise the user is asked whether to stop them and quit: on "stay", false; on "stop", every operation is
    /// cancelled, their open questions are answered as cancelled, and their ends are awaited (at most
    /// <paramref name="timeout"/>, <see cref="StopOperationsTimeout"/> by default) so each removes its unfinished part
    /// file; then true.</summary>
    public async Task<bool> ConfirmCloseAsync(TimeSpan? timeout = null)
    {
        if (!await Services.Windows.CloseAllAsync()) return false;
        var operations = Services.Operations.Operations;
        if (operations.Count == 0) return true;
        if (!await Services.Dialogs.ConfirmQuitAsync(operations.Count)) return false;

        operations = Services.Operations.Operations; // some may have ended while the question was open
        foreach (var operation in operations) operation.Cancel();
        Interaction.Close();
        var ended = Task.WhenAll(operations.Select(o => (Task)o.Completion));
        try
        {
            await ended.WaitAsync(timeout ?? StopOperationsTimeout);
        }
        catch (TimeoutException)
        {
            Services.Log.Write(new TimeoutException("File operations did not stop in time before quitting."), "quit");
        }
        catch (Exception)
        {
            // An operation that failed has ended all the same.
        }
        return true;
    }

    /// <summary>The main window is closing: questions of running operations are answered as cancelled, so no
    /// operation waits for a dialog that will never be shown, Quick Look and the quick-view panel close, and a
    /// command still running in the command line is killed.</summary>
    public void OnWindowClosing()
    {
        Interaction.Close();
        Shell.CloseQuickLook();
        QuickView.Close();
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
        void On(string id, Action action, Func<bool>? canExecute = null) => Commands.Register(id, () =>
        {
            action();
            return Task.CompletedTask;
        }, canExecute);

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
        Commands.Register(CommandIds.NavEnter, () => Panel().EnterAsync());
        Commands.Register(CommandIds.NavBack, () => ActiveTabs().GoBackAsync());
        Commands.Register(CommandIds.NavForward, () => ActiveTabs().GoForwardAsync());
        On(CommandIds.PathEdit, () => ActiveSide.BeginPathEdit());
        On(CommandIds.CmdlineFocus, () => CommandLineFocusRequested?.Invoke(this, EventArgs.Empty));
        On(CommandIds.SelectToggleDown, () => Panel().ToggleSelectAndMoveDown());
        On(CommandIds.SelectAll, () => Panel().SelectAll());
        On(CommandIds.SelectInvert, () => Panel().InvertSelection());
        Commands.Register(CommandIds.TabNew, () => ActiveTabs().NewTabAsync());
        Commands.Register(CommandIds.TabDuplicate, () => ActiveTabs().NewTabAsync());
        // A pinned tab, and the only one, cannot be closed: PanelTabsViewModel.CanClose decides, and the commands are
        // off then (no error is shown).
        On(CommandIds.TabClose, () => ActiveTabs().CloseActive(),
            () => Workspace.IsLoaded && ActiveTabs().CanClose(ActiveTabs().Active));
        On(CommandIds.TabCloseOthers, () => ActiveTabs().CloseOthers(ActiveTabs().Active),
            () => Workspace.IsLoaded && ActiveTabs().CanCloseOthers(ActiveTabs().Active));
        On(CommandIds.TabTogglePin, () => ActiveTabs().Active.IsPinned = !ActiveTabs().Active.IsPinned,
            () => Workspace.IsLoaded);
        On(CommandIds.TabNext, () => ActiveTabs().Next());
        On(CommandIds.TabPrev, () => ActiveTabs().Previous());
        On(CommandIds.FavoritesShow, () => FavoritesMenuRequested?.Invoke(this, EventArgs.Empty));
        On(CommandIds.ViewToggleHidden, () => Workspace.ShowHidden = !Workspace.ShowHidden);
        On(CommandIds.ViewQuickView, QuickView.Toggle, () => Workspace.IsLoaded);
        On(CommandIds.SortByName, () => ActiveSide.SortBy(SortColumn.Name));
        On(CommandIds.SortByExtension, () => ActiveSide.SortBy(SortColumn.Extension));
        On(CommandIds.SortBySize, () => ActiveSide.SortBy(SortColumn.Size));
        On(CommandIds.SortByDate, () => ActiveSide.SortBy(SortColumn.Modified));
        On(CommandIds.SortByMode, () => ActiveSide.SortBy(SortColumn.Mode));
        On(CommandIds.SortReverse, () => ActiveSide.ReverseSort());
        // The window starts from the key map in effect; an open one comes to the front instead.
        Commands.Register(CommandIds.AppSettings,
            () => Services.Windows.ShowSettingsAsync(new SettingsViewModel(Settings, KeyMap)));
        // Only on the local disk (not in archives, result sets or elsewhere). The availability flips with the active
        // panel's folder being local, exactly as "open terminal"'s does, whose flips ShellCommands announces.
        On(CommandIds.SearchFiles, ShowSearch, () => Workspace.IsLoaded && ShellCommands.IsLocal(Panel()));
        Commands.RaiseCanExecuteChanged();
    }

    /// <summary>The search window for the active panel's folder: the window open already comes forward, for a new
    /// folder with a new search that keeps the criteria typed.</summary>
    private void ShowSearch()
    {
        var root = Workspace.ActivePanel.CurrentPath;
        if (_search is { IsDisposed: false } open && open.Root == root)
        {
            Services.Windows.ShowSearch(open);
            return;
        }
        var search = new SearchViewModel(Services.Providers, root, GoToFoundAsync,
            (paths, title) => ShowFoundAsync(root, paths, title));
        if (_search is { IsDisposed: false } previous) search.CopyCriteriaFrom(previous);
        _search = search;
        Services.Windows.ShowSearch(search);
    }

    /// <summary>A search result in the active panel: its folder, with the cursor on it.</summary>
    private Task GoToFoundAsync(FileEntry entry) =>
        entry.Path.Parent is { } folder ? Workspace.ActivePanel.NavigateToAsync(folder, entry.Name) : Task.CompletedTask;

    /// <summary>Search results as a folder of <see cref="SearchResults"/>, shown in the active panel; up from it
    /// leads to <paramref name="root"/>.</summary>
    private Task ShowFoundAsync(VfsPath root, IReadOnlyList<VfsPath> paths, string title) =>
        Workspace.ActivePanel.NavigateToAsync(SearchResults.Add(root, title, paths));

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WorkspaceViewModel.ActiveSide):
            case nameof(WorkspaceViewModel.ActivePanel):
                OnPropertyChanged(nameof(ActiveSide));
                Left.OnActiveSideChanged();
                Right.OnActiveSideChanged();
                UpdateTabAvailability();
                break;
            case nameof(WorkspaceViewModel.View):
                Left.OnViewChanged();
                Right.OnViewChanged();
                break;
        }
    }

    private void OnQuickViewPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(QuickViewViewModel.IsOpen) or nameof(QuickViewViewModel.Side)
            or nameof(QuickViewViewModel.Viewer))) return;
        Left.OnQuickViewChanged();
        Right.OnQuickViewChanged();
    }

    /// <summary>A side's tabs, active tab or a tab's pin changed: closing tabs may have become possible or not.</summary>
    private void OnTabsStateChanged(object? sender, EventArgs e) => UpdateTabAvailability();

    /// <summary>Raises <see cref="CommandRegistry.RaiseCanExecuteChanged"/> only when closing tabs became possible or
    /// impossible.</summary>
    private void UpdateTabAvailability()
    {
        bool[] available = [Commands.CanExecute(CommandIds.TabClose), Commands.CanExecute(CommandIds.TabCloseOthers)];
        if (available.SequenceEqual(_tabAvailable)) return;
        _tabAvailable = available;
        Commands.RaiseCanExecuteChanged();
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

    /// <summary>Any thread. An operation that ended may have changed the free space (which no volume event reports):
    /// the volumes are read again whenever the queue changes. Telling an end from a start would race with operations
    /// that end at once; an extra read is cheap, and only the latest read is kept.</summary>
    private void OnOperationsChanged(object? sender, EventArgs e)
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
