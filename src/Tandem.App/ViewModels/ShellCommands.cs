using System.ComponentModel;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.App.ViewModels.Editing;
using Tandem.App.ViewModels.Viewing;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.Local;

namespace Tandem.App.ViewModels;

/// <summary>The applications that can open <paramref name="Entry"/>, as asked for it.</summary>
public sealed record OpenWithApps(FileEntry Entry, IReadOnlyList<AppInfo> Apps);

/// <summary>
/// The commands that hand items to macOS: Quick Look (Cmd+Y), Reveal in Finder, a terminal in the current folder,
/// and "Open With" (menu only); Enter on a file opens it with its default application. Also the built-in viewer (F3),
/// the text editor (F4: the external editor of the settings for a local file when one is set, else the built-in one) and the properties dialog (Cmd+I). Also tells the user when a panel's file cannot be opened as a folder, or is not one.
/// </summary>
/// <remarks>
/// Created and used on the UI thread. Everything but the viewer, the editor and the properties dialog works for the
/// local provider only and is disabled elsewhere: for an item, its own provider counts (search results list local
/// items in a folder that is not on the disk); for the terminal, the panel's folder. The viewer and the editor open
/// a file of any provider off the UI thread, in a window of <see cref="IWindowService"/> (a viewer still opening when the commands are disposed is
/// closed); F4 on a file already open there brings its window forward; Enter on a file elsewhere opens a copy of it made in the <see cref="TempFileStore"/> (off the UI
/// thread): changes to the copy do not go back. Only the panels shown (active and passive) are listened to: what a
/// panel no longer shown reports (a file activated, not a folder, a failed open) is dropped on purpose, since the user
/// has moved on, and a copy still being made for such a panel is removed instead of opened. A launch that fails
/// (<see cref="IOException"/>), at once or when the launcher reports it a moment later (no application for the file,
/// an item that is gone), is told to the user in words, once; anything unexpected goes to the failure handler. While Quick Look is open, moving the cursor
/// shows the new item after a short pause (holding an arrow key starts one preview, not one per row); going to
/// another folder, switching the active panel or closing the window closes it.
/// <see cref="CommandRegistry.RaiseCanExecuteChanged"/> is raised only when one of these commands' availability flips.
/// </remarks>
public sealed class ShellCommands : IDisposable
{
    /// <summary>How long the cursor must rest before an open preview follows it.</summary>
    public static readonly TimeSpan QuickLookFollowDelay = TimeSpan.FromMilliseconds(150);

    private static readonly string[] Ids =
    [
        CommandIds.FileView, CommandIds.FileEdit, CommandIds.FileQuickLook, CommandIds.FileProperties,
        CommandIds.ShellReveal, CommandIds.ShellOpenTerminal, CommandIds.ShellOpenWith,
    ];

    private readonly WorkspaceViewModel _workspace;
    private readonly IShellIntegration _shell;
    private readonly IQuickLook _quickLook;
    private readonly IDialogService _dialogs;
    private readonly IWindowService _windows;
    private readonly INativeCopy? _nativeCopy;
    private readonly SettingsService? _settings;
    private readonly IProcessRunner? _processes;
    private readonly CommandRegistry _commands;
    private readonly ProviderRegistry _providers;
    private readonly IFileMetadataService? _metadata;
    private readonly TempFileStore _temp;
    private readonly QuarantineCarrier? _quarantine;
    private readonly Action<Exception> _reportFailure;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly List<PanelViewModel> _watched = [];
    private readonly CancellationTokenSource _lifetime = new();
    private PanelViewModel? _activePanel;
    private bool[] _available = [];
    private IReadOnlyList<string> _previewed = [];
    private CancellationTokenSource? _follow;
    private bool _propertiesOpen;
    private bool _disposed;

    /// <param name="quickLookDelay">Waits <see cref="QuickLookFollowDelay"/> before an open preview follows the
    /// cursor; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when null.</param>
    /// <param name="nativeCopy">Gives a local file saved by the editor the original's metadata (tags, extended
    /// attributes); none is copied when null.</param>
    /// <param name="settings">Names the external editor F4 starts (with <paramref name="processes"/>) for a local
    /// file; the built-in editor always when either is null.</param>
    public ShellCommands(WorkspaceViewModel workspace, IShellIntegration shell, IQuickLook quickLook,
        IDialogService dialogs, IWindowService windows, CommandRegistry commands, ProviderRegistry providers, IFileMetadataService? metadata,
        TempFileStore temp, Action<Exception> reportFailure,
        Func<TimeSpan, CancellationToken, Task>? quickLookDelay = null, INativeCopy? nativeCopy = null,
        SettingsService? settings = null, IProcessRunner? processes = null, QuarantineCarrier? quarantine = null)
    {
        _quarantine = quarantine;
        _nativeCopy = nativeCopy;
        _settings = settings;
        _processes = processes;
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _quickLook = quickLook ?? throw new ArgumentNullException(nameof(quickLook));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _metadata = metadata;
        _temp = temp ?? throw new ArgumentNullException(nameof(temp));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        _delay = quickLookDelay ?? Task.Delay;

        commands.Register(CommandIds.FileView, ViewAsync, () => CursorEntry() is { IsDirectoryLike: false });
        commands.Register(CommandIds.FileQuickLook, ToggleQuickLookAsync, CanQuickLook);
        commands.Register(CommandIds.FileEdit, EditAsync, () => CursorEntry() is { IsDirectoryLike: false });
        commands.Register(CommandIds.FileProperties, ShowPropertiesAsync, () => CursorEntry() is not null);
        commands.Register(CommandIds.ShellReveal, RevealAsync, () => LocalCursorEntry() is not null);
        commands.Register(CommandIds.ShellOpenTerminal, OpenTerminalAsync, IsActiveLocal);
        commands.Register(CommandIds.ShellOpenWith, RequestOpenWithMenu, () => LocalCursorEntry() is not null);

        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        WatchPanels();
    }

    /// <summary>"Open With" was chosen from the main menu: the view shows the applications of
    /// <see cref="QueryOpenWithAsync"/> at the cursor.</summary>
    public event EventHandler? OpenWithMenuRequested;

    /// <summary>
    /// The applications that can open the item under the cursor of the active panel, with that item as it was when
    /// the query started. Asks the system each time, off the UI thread (the query reads the disk and LaunchServices).
    /// Null when the cursor is not on a local item, or when it moved to another item (or panel) while the query ran:
    /// the answer would then belong to the wrong item. A failing query is told to the user
    /// (<see cref="IOException"/>) or reported, and gives no applications; never throws.
    /// </summary>
    public async Task<OpenWithApps?> QueryOpenWithAsync()
    {
        if (Active is not { } panel || LocalCursorEntry() is not { } entry) return null;
        var (shell, path) = (_shell, entry.Path.ToOsPath());
        IReadOnlyList<AppInfo> apps = [];
        try
        {
            apps = await Task.Run(() => shell.GetAppsFor(path));
        }
        catch (IOException e)
        {
            await TellAsync(Loc.Format("Shell_AppsFailed", entry.Name), e);
        }
        catch (Exception e)
        {
            _reportFailure(e);
        }
        return Active == panel && LocalCursorEntry()?.Path == entry.Path ? new OpenWithApps(entry, apps) : null;
    }

    /// <summary>Opens <paramref name="entry"/> (the item the applications were asked for) with
    /// <paramref name="app"/>; a failure is told to the user.</summary>
    public void OpenWith(FileEntry entry, AppInfo app)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(app);
        if (entry.Path.ProviderId != VfsPath.LocalProviderId) return;
        _ = RunAsync(() => _shell.OpenWith(entry.Path.ToOsPath(), app), Loc.Format("Shell_OpenFailed", entry.Name));
    }

    /// <summary>The button of an access-denied panel: the system settings page for Full Disk Access.</summary>
    public void OpenAccessSettings() =>
        _ = RunAsync(_shell.OpenFullDiskAccessSettings, Loc.Get("Shell_AccessSettingsFailed"));

    /// <summary>Whether a folder of <paramref name="panel"/> is on the local disk.</summary>
    public static bool IsLocal(PanelViewModel? panel) =>
        panel is not null && panel.CurrentPath != default && panel.CurrentPath.ProviderId == VfsPath.LocalProviderId;

    /// <summary>Closes Quick Look (the main window is closing).</summary>
    public void CloseQuickLook()
    {
        CancelFollow();
        _previewed = [];
        try
        {
            _quickLook.Close();
        }
        catch (Exception e)
        {
            _reportFailure(e);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        CancelFollow();
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        foreach (var panel in _watched)
        {
            panel.PropertyChanged -= OnPanelPropertyChanged;
            panel.FileActivated -= OnFileActivated;
            panel.NotAFolder -= OnNotAFolder;
            panel.OpenFailed -= OnOpenFailed;
        }
        _watched.Clear();
    }

    // ---- Targets and availability -----------------------------------------------------------------------

    private PanelViewModel? Active => _workspace.IsLoaded ? _workspace.ActivePanel : null;

    private bool IsActiveLocal() => IsLocal(Active);

    private FileEntry? CursorEntry() =>
        Active is { CursorRow: { IsParentLink: false, Entry: { } entry } } panel && panel.CurrentPath != default
            ? entry
            : null;

    private FileEntry? LocalCursorEntry() => CursorEntry() is { } entry && IsLocal(entry) ? entry : null;

    private static bool IsLocal(FileEntry entry) => entry.Path.ProviderId == VfsPath.LocalProviderId;

    /// <summary>In a local folder (with nothing to show, a toggle closes the preview), or on a local item.</summary>
    private bool CanQuickLook() => IsActiveLocal() || LocalCursorEntry() is not null;

    /// <summary>The local items among the operation targets.</summary>
    private IReadOnlyList<string> QuickLookTargets() =>
        Active is { } panel && panel.CurrentPath != default
            ? panel.GetOperationTargets().Where(IsLocal).Select(e => e.Path.ToOsPath()).ToArray()
            : [];

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.ActivePanel) or nameof(WorkspaceViewModel.PassivePanel))
            WatchPanels();
    }

    private void WatchPanels()
    {
        foreach (var panel in _watched)
        {
            panel.PropertyChanged -= OnPanelPropertyChanged;
            panel.FileActivated -= OnFileActivated;
            panel.NotAFolder -= OnNotAFolder;
            panel.OpenFailed -= OnOpenFailed;
        }
        _watched.Clear();
        if (_workspace.IsLoaded)
        {
            foreach (var panel in new[] { _workspace.ActivePanel, _workspace.PassivePanel })
            {
                panel.PropertyChanged += OnPanelPropertyChanged;
                panel.FileActivated += OnFileActivated;
                panel.NotAFolder += OnNotAFolder;
                panel.OpenFailed += OnOpenFailed;
                _watched.Add(panel);
            }
            if (_activePanel is not null && _activePanel != _workspace.ActivePanel) ClosePreview();
            _activePanel = _workspace.ActivePanel;
        }
        UpdateAvailability();
    }

    private void OnPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PanelViewModel.CurrentPath):
                if (sender == Active) ClosePreview();
                UpdateAvailability();
                break;
            case nameof(PanelViewModel.CursorIndex):
                if (sender == Active) FollowCursor();
                UpdateAvailability();
                break;
        }
    }

    private void UpdateAvailability()
    {
        var available = Ids.Select(_commands.CanExecute).ToArray();
        if (available.SequenceEqual(_available)) return;
        _available = available;
        _commands.RaiseCanExecuteChanged();
    }

    // ---- Opening ----------------------------------------------------------------------------------------

    /// <summary>Enter on a file (or an application bundle): its default application; a file that is not on the local
    /// disk is copied to the temporary files first and the copy is opened.</summary>
    private void OnFileActivated(object? sender, FileEntry entry)
    {
        var failureText = Loc.Format("Shell_OpenFailed", entry.Name);
        if (entry.Path.ProviderId == VfsPath.LocalProviderId)
            _ = RunAsync(() => _shell.OpenDefault(entry.Path.ToOsPath()), failureText);
        else
            _ = OpenCopyAsync(sender as PanelViewModel, entry, failureText);
    }

    /// <summary>Copies <paramref name="entry"/> into the temporary files off the UI thread and opens the copy, unless
    /// <paramref name="panel"/> is no longer shown by then: the copy is removed. A copy that fails
    /// (<see cref="IOException"/>) is told as <paramref name="failureText"/>. Never throws.</summary>
    private async Task OpenCopyAsync(PanelViewModel? panel, FileEntry entry, string failureText)
    {
        string copy;
        try
        {
            var (temp, provider, quarantine) = (_temp, _providers.For(entry.Path), _quarantine);
            copy = await Task.Run(async () =>
            {
                var made = await temp.MaterializeAsync(provider, entry.Path, null, CancellationToken.None);
                quarantine?.MarkCopy(entry.Path, made); // what came from a downloaded archive stays marked
                return made;
            });
        }
        catch (IOException e)
        {
            if (!_disposed) await TellAsync(failureText, e);
            return;
        }
        catch (Exception e)
        {
            _reportFailure(e);
            return;
        }
        if (_disposed || panel is null || !_watched.Contains(panel))
        {
            Release(copy);
            return;
        }
        await RunAsync(() => _shell.OpenDefault(copy), failureText);
    }

    /// <summary>Removes a temporary copy nobody will open; one that cannot be removed goes with the next
    /// clear.</summary>
    private void Release(string copy)
    {
        try
        {
            _temp.Release(copy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // TempFileStore.Clear removes it at exit or the next start
        }
    }

    /// <summary>Cmd+Down on a file that does not open as a folder.</summary>
    private void OnNotAFolder(object? sender, FileEntry entry) =>
        _ = TellAsync(Loc.Format("Panel_NotAFolder", entry.Name), detail: null);

    /// <summary>A file that opens as a folder could not be opened: told in words, the system's as detail.</summary>
    private void OnOpenFailed(object? sender, Exception error) =>
        _ = TellAsync(OperationTexts.ForFailure(error), error.Message);

    /// <summary>F4: a file already open in a built-in editor window brings that window forward. Otherwise a local
    /// file goes to the external editor when the settings name one (started in the file's folder, the file its one
    /// argument; a start that fails, or an application that open cannot launch, is told to the user), and any other
    /// file opens in the built-in editor, read off the UI thread. A file the editor refuses (too large, binary) or cannot read is told to the user.</summary>
    private async Task EditAsync()
    {
        if (CursorEntry() is not { IsDirectoryLike: false } entry || await _windows.TryActivateEditorAsync(entry.Path))
            return;
        var localCopy = _providers.For(entry.Path).Capabilities.HasFlag(ProviderCapabilities.LocalCopyEdit);
        if (!localCopy && entry.Path.ProviderId == VfsPath.LocalProviderId && _settings?.Current.ExternalEditor is { } program &&
            _processes is not null)
        {
            await EditExternallyAsync(program, entry.Path.ToOsPath());
            return;
        }
        EditorViewModel editor;
        try
        {
            editor = localCopy
                ? await EditorViewModel.OpenRemoteAsync(_providers, _temp, entry.Path, _lifetime.Token)
                : await EditorViewModel.OpenAsync(_providers, entry.Path, CancellationToken.None, _nativeCopy);
        }
        catch (EditorRefusedException e)
        {
            var key = e.Reason == EditorRefusal.TooLarge ? "Editor_TooLarge" : "Editor_Binary";
            if (!_disposed) await TellAsync(Loc.Format(key, entry.Name), detail: null);
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!_disposed) await TellAsync(Loc.Format("Shell_OpenFailed", entry.Name), e);
            return;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        // No await between this check and showing: an editor of the same file opened meanwhile is reused.
        if (_disposed || _windows.TryActivateEditorFor(editor.Target))
        {
            await editor.ReleaseCopyAsync();
            return;
        }
        _windows.ShowEditor(editor);
    }

    private async Task EditExternallyAsync(string program, string path)
    {
        var failureText = Loc.Format("UserCommand_StartFailed", program);
        Task<string?> verdict;
        try
        {
            var folder = Path.GetDirectoryName(path) ?? Path.GetPathRoot(path) ?? "/";
            verdict = ProgramLauncher.Start(_processes!,
                ProgramLauncher.Build(program, [path], folder, _workspace.HomeDirectory));
        }
        catch (Exception e) when (ProgramLauncher.IsStartFailure(e))
        {
            await TellAsync(failureText, e);
            return;
        }
        // An application that open could not launch is told a moment later, with open's words; F4 does not wait.
        _ = ObserveLaunchAsync(verdict, failureText);
    }

    private async Task ObserveLaunchAsync(Task<string?> verdict, string failureText)
    {
        if (await verdict is { } output && !_disposed) await TellAsync(failureText, output.Length > 0 ? output : null);
    }

    /// <summary>F3: the file under the cursor in the built-in viewer, opened off the UI thread. A file that cannot be
    /// read is told to the user.</summary>
    private async Task ViewAsync()
    {
        if (_disposed || CursorEntry() is not { IsDirectoryLike: false } entry) return;
        ViewerViewModel viewer;
        try
        {
            viewer = await ViewerViewModel.OpenAsync(_providers, _temp, entry.Path, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return; // the app is closing
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!_disposed) await TellAsync(Loc.Format("Shell_OpenFailed", entry.Name), e);
            return;
        }
        if (_disposed)
        {
            await viewer.DisposeAsync();
            return;
        }
        _windows.ShowViewer(viewer);
    }

    private Task RevealAsync() =>
        LocalCursorEntry() is { } entry
            ? RunAsync(() => _shell.RevealInFinder(entry.Path.ToOsPath()), Loc.Format("Shell_RevealFailed", entry.Name))
            : Task.CompletedTask;

    private Task OpenTerminalAsync() =>
        Active is { } panel && IsLocal(panel)
            ? RunAsync(() => _shell.OpenTerminal(panel.CurrentPath.ToOsPath()),
                Loc.Format("Shell_TerminalFailed", panel.CurrentPath.Display()))
            : Task.CompletedTask;

    private Task RequestOpenWithMenu()
    {
        OpenWithMenuRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    /// <summary>Runs a shell call: a launch failure is told to the user as <paramref name="failureText"/> with the
    /// system's words as detail; anything else goes to the failure handler. Never throws.</summary>
    private async Task RunAsync(Func<Task> launch, string failureText)
    {
        Task verdict;
        try
        {
            verdict = launch();
        }
        catch (IOException e)
        {
            await TellAsync(failureText, e);
            return;
        }
        catch (Exception e)
        {
            _reportFailure(e);
            return;
        }
        // The launcher's own verdict comes later: the command does not wait for it.
        _ = ObserveAsync(verdict, failureText);
    }

    /// <summary>Waits (on the UI thread, without blocking it) for the launcher's verdict; a failure it reports
    /// (<see cref="IOException"/>, its words as detail) is told to the user as <paramref name="failureText"/>, anything
    /// else goes to the failure handler. Never throws.</summary>
    private async Task ObserveAsync(Task verdict, string failureText)
    {
        try
        {
            await verdict;
        }
        catch (IOException e)
        {
            if (!_disposed) await TellAsync(failureText, e);
        }
        catch (Exception e)
        {
            _reportFailure(e);
        }
    }

    private Task TellAsync(string text, Exception e) => TellAsync(text, e.Message);

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

    // ---- Quick Look --------------------------------------------------------------------------------------

    private async Task ToggleQuickLookAsync()
    {
        if (!CanQuickLook()) return;
        CancelFollow();
        var targets = QuickLookTargets();
        try
        {
            _quickLook.Toggle(targets);
            _previewed = _quickLook.IsOpen ? targets : [];
        }
        catch (IOException e)
        {
            _previewed = [];
            await TellAsync(Loc.Get("Shell_QuickLookFailed"), e);
        }
    }

    /// <summary>The cursor moved: an open preview shows the new targets once the cursor rests.</summary>
    private void FollowCursor()
    {
        if (!_quickLook.IsOpen || _previewed.Count == 0) return;
        CancelFollow();
        var follow = new CancellationTokenSource();
        _follow = follow;
        _ = FollowAsync(follow);
    }

    private async Task FollowAsync(CancellationTokenSource follow)
    {
        try
        {
            await _delay(QuickLookFollowDelay, follow.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            _reportFailure(e);
            return;
        }
        if (follow.IsCancellationRequested || _follow != follow || _disposed) return;
        _follow = null;
        follow.Dispose();

        // The user may have closed the preview meanwhile; it is not reopened then.
        if (!_quickLook.IsOpen) return;
        var targets = QuickLookTargets();
        if (targets.Count == 0 || targets.SequenceEqual(_previewed, StringComparer.Ordinal)) return;
        try
        {
            _quickLook.Close();
            _quickLook.Toggle(targets);
            _previewed = targets;
        }
        catch (IOException e)
        {
            _previewed = [];
            await TellAsync(Loc.Get("Shell_QuickLookFailed"), e);
        }
        catch (Exception e)
        {
            _reportFailure(e);
        }
    }

    private void ClosePreview()
    {
        CancelFollow();
        if (_previewed.Count == 0 && !_quickLook.IsOpen) return;
        CloseQuickLook();
    }

    private void CancelFollow()
    {
        if (_follow is not { } follow) return;
        _follow = null;
        follow.Cancel();
        follow.Dispose();
    }

    // ---- Properties --------------------------------------------------------------------------------------

    /// <summary>Loads the item's details, shows the dialog, and refreshes the panel when anything was applied, however
    /// the dialog was closed. The task spans the whole flow, so the registry refuses a second Cmd+I until it ends;
    /// the flag also keeps a direct second call from stacking another dialog.</summary>
    private async Task ShowPropertiesAsync()
    {
        if (_propertiesOpen || Active is not { } panel || CursorEntry() is not { } entry) return;
        _propertiesOpen = true;
        try
        {
            var folder = panel.CurrentPath;
            var properties = new PropertiesViewModel(entry, _providers.For(entry.Path), _metadata, _reportFailure);
            await properties.LoadAsync();
            await _dialogs.ShowPropertiesAsync(properties);
            // Show the new mode; the refresh keeps the cursor on the same name.
            if (properties.HasApplied && panel.CurrentPath == folder) await panel.RefreshAsync();
        }
        finally
        {
            _propertiesOpen = false;
        }
    }
}
