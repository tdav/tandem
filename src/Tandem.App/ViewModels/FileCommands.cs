using Tandem.Core.Vfs.Local;
using System.ComponentModel;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

/// <summary>
/// The commands that change files: copy (F5), move (F6), new folder (F7), delete to the Trash (F8) or permanently
/// (Shift+F8), inline rename (Shift+F6, F2), and marking by mask. Each asks through <see cref="IDialogService"/>, then
/// hands the work to the <see cref="OperationQueue"/> and returns; what happens when an operation ends is handled
/// apart (refresh, cursor, summary), and never throws into the UI: unexpected errors go to the failure handler.
/// </summary>
/// <remarks>
/// Created and used on the UI thread. What the commands apply to is <see cref="PanelViewModel.GetOperationTargets"/>
/// of the active panel; nothing to apply to (the cursor on "..", an empty folder) disables them. The targets may lie
/// outside the panel's folder (search results): moving, deleting and renaming need every target's own provider to
/// write, the Trash is used when every target's provider has one, and a relative destination starts at the first
/// target's folder. A new folder is made in the panel's folder, so it needs that folder's provider to write. Copy and
/// move go to the passive panel's source and are disabled while that panel shows no folder or an error.
/// Drag and drop goes through the same copy/move flow
/// (<see cref="CopyOrMoveAsync(IReadOnlyList{VfsPath}, VfsPath, bool)"/>).
/// <see cref="CommandRegistry.RaiseCanExecuteChanged"/> is raised only when one of these commands' availability
/// actually changes.
/// </remarks>
public sealed class FileCommands : IDisposable
{
    private readonly WorkspaceViewModel _workspace;
    private readonly ProviderRegistry _providers;
    private readonly OperationQueue _queue;
    private readonly IDialogService _dialogs;
    private readonly ITrash? _trash;
    private readonly INativeCopy? _nativeCopy;
    private readonly CommandRegistry _commands;
    private readonly OperationsViewModel _operations;
    private readonly IOperationInteraction _interaction;
    private readonly Action<Exception> _reportFailure;
    private readonly List<Task> _pending = [];

    /// <summary>Marks what copies and moves take out of an archive with the archive's quarantine mark; null when the
    /// platform keeps no such mark. Unpacking uses it too.</summary>
    public QuarantineCarrier? Quarantine { get; }
    private readonly List<PanelViewModel> _watched = [];
    private bool[] _available = [];
    private InlineRenameViewModel? _rename;
    private bool _askingCopyOrMove;
    private bool _disposed;

    private static readonly string[] Ids =
    [
        CommandIds.FileCopy, CommandIds.FileMove, CommandIds.FileMkdir, CommandIds.FileDelete,
        CommandIds.FileDeletePermanent, CommandIds.FileRename, CommandIds.SelectByMask, CommandIds.UnselectByMask,
    ];

    public FileCommands(WorkspaceViewModel workspace, ProviderRegistry providers, OperationQueue queue,
        IDialogService dialogs, ITrash? trash, INativeCopy? nativeCopy, CommandRegistry commands,
        OperationsViewModel operations, IOperationInteraction interaction, Action<Exception> reportFailure,
        QuarantineCarrier? quarantine = null)
    {
        Quarantine = quarantine;
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _trash = trash;
        _nativeCopy = nativeCopy;
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));

        void On(string id, Func<Task> run, Func<bool> canRun) => commands.Register(id, () => Tracked(run()), canRun);
        On(CommandIds.FileCopy, () => CopyOrMoveAsync(move: false), CanCopyOrMove);
        On(CommandIds.FileMove, () => CopyOrMoveAsync(move: true), CanMove);
        On(CommandIds.FileMkdir, MakeDirectoryAsync, CanMakeDirectory);
        On(CommandIds.FileDelete, () => DeleteAsync(permanent: false), CanDelete);
        On(CommandIds.FileDeletePermanent, () => DeleteAsync(permanent: true), CanDelete);
        On(CommandIds.FileRename, BeginRename, HasCursorEntry);
        On(CommandIds.SelectByMask, () => MarkByMaskAsync(select: true), HasRows);
        On(CommandIds.UnselectByMask, () => MarkByMaskAsync(select: false), HasRows);

        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        WatchPanels();
    }

    /// <summary>The mask the mask dialog starts with: the last one used in this session.</summary>
    public string LastMask { get; private set; } = "*.*";

    /// <summary>The inline rename editor that is open, if any.</summary>
    public InlineRenameViewModel? Rename => _rename;

    /// <summary>Completes when no command is running (a dialog may still be open in one) and every operation started
    /// has ended with its aftermath (refresh, cursor, summary) done. Exceptions are not rethrown.</summary>
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

    private Task Tracked(Task task)
    {
        _pending.RemoveAll(t => t.IsCompleted);
        _pending.Add(task);
        return task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        foreach (var panel in _watched) panel.PropertyChanged -= OnPanelPropertyChanged;
        _watched.Clear();
    }

    // ---- Availability ------------------------------------------------------------------------------------

    private PanelViewModel? Active => _workspace.IsLoaded ? _workspace.ActivePanel : null;

    private PanelViewModel? Passive => _workspace.IsLoaded ? _workspace.PassivePanel : null;

    private bool CanWrite(PanelViewModel panel) => CanWriteTo(panel.CurrentPath);

    private bool HasTargets() =>
        Active is { } panel && panel.CurrentPath != default &&
        (panel.SelectedCount > 0 || panel.CursorRow is { IsParentLink: false });

    private bool HasCursorEntry() =>
        Active is { CursorRow: { IsParentLink: false, Entry: { } entry } } panel && panel.CurrentPath != default &&
        CanWriteTo(entry.Path);

    private bool HasRows() => Active is { TotalCount: > 0 };

    private bool CanMakeDirectory() => Active is { Error: null } panel && CanWrite(panel);

    /// <summary>Copying needs a writable destination only; moving and deleting change the source too.</summary>
    private bool CanCopyOrMove() =>
        HasTargets() && Passive is { Error: null } passive && CanWrite(passive);

    private bool CanMove() => CanCopyOrMove() && CanWriteTargets(Active!);

    private bool CanDelete() => HasTargets() && CanWriteTargets(Active!);

    /// <summary>Whether every operation target's own provider can write. A panel listing only its folder's entries
    /// answers by that folder, without going through the targets.</summary>
    private bool CanWriteTargets(PanelViewModel panel) =>
        panel.HasForeignEntries
            ? panel.GetOperationTargets().All(t => CanWriteTo(t.Path))
            : CanWrite(panel);

    /// <summary>Whether every target's provider has the Trash.</summary>
    private bool CanTrash(IReadOnlyList<FileEntry> targets) =>
        _trash is not null &&
        targets.All(t => _providers.For(t.Path).Capabilities.HasFlag(ProviderCapabilities.Trash));

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
            or nameof(PanelViewModel.CurrentPath) or nameof(PanelViewModel.Error) or nameof(PanelViewModel.TotalCount))
            UpdateAvailability();
    }

    /// <summary>Raises <see cref="CommandRegistry.RaiseCanExecuteChanged"/> only when one of these commands became
    /// available or unavailable.</summary>
    private void UpdateAvailability()
    {
        var available = Ids.Select(_commands.CanExecute).ToArray();
        if (available.SequenceEqual(_available)) return;
        _available = available;
        _commands.RaiseCanExecuteChanged();
    }

    // ---- Copy and move -----------------------------------------------------------------------------------

    /// <summary>F5/F6: the active panel's targets to the passive panel's folder.</summary>
    private async Task CopyOrMoveAsync(bool move)
    {
        if (!(move ? CanMove() : CanCopyOrMove()) || Active is not { } active || Passive is not { } passive) return;
        var targets = active.GetOperationTargets();
        if (targets.Count == 0) return;
        await CopyOrMoveCoreAsync(targets.Select(t => t.Path).ToArray(), targets[0].Path.Parent ?? active.CurrentPath,
            passive.CurrentPath, move);
    }

    /// <summary>Whether items can be copied into <paramref name="directory"/>, or the item at that path changed: its
    /// provider can write.</summary>
    public bool CanWriteTo(VfsPath directory) =>
        directory != default && _providers.For(directory).Capabilities.HasFlag(ProviderCapabilities.Write);

    /// <summary>Whether a copy or move dialog (F5, F6 or a drop) is open now: another one is not started.</summary>
    public bool IsAskingCopyOrMove => _askingCopyOrMove;

    /// <summary>
    /// The copy/move flow shared by F5/F6 and drag and drop: the ordinary copy or move dialog, its text proposed from
    /// <paramref name="destinationDirectory"/> and referring to that folder's provider (a relative text starts at the
    /// items' folder); then the operation through the queue and the usual aftermath. Nothing happens unless the user
    /// accepts the dialog. True when an operation was started.
    /// </summary>
    /// <param name="move">The action; with <paramref name="offerBoth"/> only the one pre-selected.</param>
    /// <param name="offerBoth">The dialog offers both Copy and Move (a drop), and the user's choice in it decides.</param>
    /// <remarks>Does nothing (false) for no sources, sources on different providers, a destination that cannot be
    /// written, while another copy/move dialog is open, or (once Move is chosen) a move of items whose provider
    /// cannot be written. Waits for the dialog, not the operation.</remarks>
    public Task<bool> CopyOrMoveAsync(IReadOnlyList<VfsPath> sources, VfsPath destinationDirectory, bool move,
        bool offerBoth = false)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0 || sources.Any(s => s.ProviderId != sources[0].ProviderId) ||
            !CanWriteTo(destinationDirectory) || (move && !offerBoth && !CanMoveFrom(sources)) || _askingCopyOrMove)
            return Task.FromResult(false);
        var task = CopyOrMoveCoreAsync(sources.ToArray(), sources[0].Parent ?? sources[0], destinationDirectory, move,
            offerBoth);
        Tracked(task);
        return task;
    }

    private bool CanMoveFrom(IReadOnlyList<VfsPath> sources) => sources.All(CanWriteTo);

    /// <param name="baseDirectory">Where a relative destination text starts (the items' folder).</param>
    private async Task<bool> CopyOrMoveCoreAsync(IReadOnlyList<VfsPath> sources, VfsPath baseDirectory,
        VfsPath destinationDirectory, bool move, bool offerBoth = false)
    {
        // Drops do not go through the command registry: this flag keeps a drop, F5 and F6 from stacking dialogs.
        if (_askingCopyOrMove) return false;
        var single = sources.Count == 1 ? sources[0].Name : null;
        var to = destinationDirectory;
        // Shown as the user types paths (on Windows C:\\x); DestinationParser reads that form back.
        var proposal = single is null ? to.Display() : to.Combine(single).Display();
        var probe = OffTheUiThread(DestinationParser.ProbeOf(_providers));
        Task<DestinationParseResult> Parse(string text) => DestinationParser.ParseAsync(text, baseDirectory, to,
            sources.Count, single, _workspace.HomeDirectory, probe);

        CopyMoveAnswer? answer;
        _askingCopyOrMove = true;
        try
        {
            answer = await _dialogs.AskCopyMoveAsync(new CopyMoveRequest(move, sources.Count, single, proposal,
                async text => (await Parse(text)).Error, offerBoth));
        }
        finally
        {
            _askingCopyOrMove = false;
        }
        if (answer is null) return false;
        if (offerBoth) move = answer.IsMove;
        if (move && !CanMoveFrom(sources)) return false;

        // The dialog closes only on text its validator accepted; things may have changed since, so look again.
        var parsed = await Parse(answer.Destination);
        if (parsed.Target is not { } target)
        {
            await _dialogs.ShowMessageAsync(parsed.Error!);
            return false;
        }
        if (target.CreateDirectory && !await CreateDirectoryAsync(target.Directory)) return false;
        // A folder made for the copy shows up in its parent, which the destination panel may be showing.
        IReadOnlyList<VfsPath> alsoChanged = target.CreateDirectory
            ? [target.Directory.Parent ?? target.Directory, to]
            : [];

        var options = new CopyOptions(answer.VerifyContent, target.NewName);
        var itemWritten = Quarantine?.ForSources(sources);
        FileOperation operation = move
            ? new MoveOperation(_providers, sources, target.Directory, options, _interaction, _nativeCopy)
            {
                ItemWritten = itemWritten,
            }
            : new CopyOperation(_providers, sources, target.Directory, options, _interaction, _nativeCopy)
            {
                ItemWritten = itemWritten,
            };
        _ = Start(operation, answer.RunImmediately, alsoChanged: alsoChanged);
        return true;
    }

    private static Func<VfsPath, CancellationToken, Task<DestinationKind>> OffTheUiThread(
        Func<VfsPath, CancellationToken, Task<DestinationKind>> probe) =>
        (path, ct) => Task.Run(() => probe(path, ct), ct);

    /// <summary>Creates a folder (and its missing parents) off the UI thread; a failure is shown. True when it
    /// exists afterwards.</summary>
    private async Task<bool> CreateDirectoryAsync(VfsPath directory)
    {
        var provider = _providers.For(directory);
        try
        {
            await Task.Run(() => provider.CreateDirectoryAsync(directory, CancellationToken.None));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowMessageAsync(OperationTexts.ForError(directory, e));
            return false;
        }
    }

    // ---- New folder --------------------------------------------------------------------------------------

    private async Task MakeDirectoryAsync()
    {
        if (!CanMakeDirectory() || Active is not { } panel) return;
        var directory = panel.CurrentPath;
        var provider = _providers.For(directory);

        async Task<string?> Validate(string text)
        {
            if (FolderSegments(text, out var error) is not { } segments) return error;
            var target = Combine(directory, segments);
            try
            {
                var existing = await Task.Run(() => provider.GetEntryAsync(target, CancellationToken.None));
                return existing is null ? null : Loc.Format("NewFolder_Exists", text);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return OperationTexts.ForError(target, e); // the dialog stays open and says why
            }
        }

        var name = await _dialogs.AskNewFolderNameAsync("", Validate);
        if (name is null) return;
        if (FolderSegments(name, out var invalid) is not { } names)
        {
            await _dialogs.ShowMessageAsync(invalid!);
            return;
        }
        if (!await CreateDirectoryAsync(Combine(directory, names))) return;

        await RefreshPanelsShowingAsync([directory], []);
        if (panel.CurrentPath == directory) FocusRow(panel, names[0]);
    }

    /// <summary>The folder names of "a/b/c" (a trailing "/" is allowed); null with a message for an empty text, a
    /// leading "/", or an empty, "." or ".." segment.</summary>
    public static string[]? FolderSegments(string text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = Loc.Get("NewFolder_Empty");
            return null;
        }
        // On Windows '\\' separates folders as well, and each name must be one Windows can hold.
        var windows = LocalPath.Current == LocalPathStyle.Windows;
        var separators = windows ? new[] { '/', '\\' } : ['/'];
        var trimmed = separators.Contains(text[^1]) ? text[..^1] : text;
        var segments = trimmed.Split(separators);
        if (separators.Contains(text[0]) ||
            segments.Any(s => s.Length == 0 || s is "." or ".." || s.Contains('\0') || (windows && !LocalPath.IsWindowsName(s))))
        {
            error = Loc.Format("NewFolder_Invalid", text);
            return null;
        }
        return segments;
    }

    private static VfsPath Combine(VfsPath directory, IEnumerable<string> segments) =>
        segments.Aggregate(directory, (path, name) => path.Combine(name));

    // ---- Delete ------------------------------------------------------------------------------------------

    private async Task DeleteAsync(bool permanent)
    {
        if (!CanDelete() || Active is not { } panel) return;
        var targets = panel.GetOperationTargets();
        if (targets.Count == 0) return;

        var toTrash = !permanent && CanTrash(targets);
        var single = targets.Count == 1 ? targets[0].Name : null;
        if (!await _dialogs.ConfirmDeleteAsync(targets.Count, single, permanent: !toTrash)) return;

        _ = Start(new DeleteOperation(_providers, targets.Select(t => t.Path).ToArray(), toTrash, _trash, _interaction),
            runImmediately: false);
    }

    // ---- Rename ------------------------------------------------------------------------------------------

    private Task BeginRename()
    {
        if (!HasCursorEntry() || Active is not { CursorRow: { IsParentLink: false, Entry: { } entry } row } panel)
            return Task.CompletedTask;
        _rename?.Cancel();
        var editor = new InlineRenameViewModel(row, name => RenameAsync(panel, entry, name), closed =>
        {
            if (_rename == closed) _rename = null;
        }, _reportFailure);
        _rename = editor;
        row.Rename = editor;
        return Task.CompletedTask;
    }

    private Task RenameAsync(PanelViewModel panel, FileEntry entry, string newName)
    {
        var directory = entry.Path.Parent!.Value;
        // Onto an existing name, the conflict answer decides what the new name ends up as (kept, overwritten, or
        // renamed again): the cursor must not guess and land on the other file.
        var nameTaken = panel.Rows.Any(r => r.Entry is { } other && other.Path.Parent == directory &&
                                            string.Equals(r.Name, newName, StringComparison.Ordinal));
        var operation = new MoveOperation(_providers, [entry.Path], directory, new CopyOptions(NewName: newName),
            _interaction, _nativeCopy);
        _ = Start(operation, runImmediately: true, afterRefresh: result =>
        {
            if (!nameTaken && result is { State: OperationState.Completed, Skipped.Count: 0 } &&
                panel.CurrentPath == directory)
                FocusRow(panel, newName);
        });
        return Task.CompletedTask;
    }

    // ---- Marking by mask ---------------------------------------------------------------------------------

    private async Task MarkByMaskAsync(bool select)
    {
        if (Active is not { } panel) return;
        var mask = await _dialogs.AskMaskAsync(select, LastMask);
        if (mask is null) return;
        LastMask = mask;
        if (select) panel.SelectByMask(mask);
        else panel.UnselectByMask(mask);
    }

    // ---- Running and finishing ---------------------------------------------------------------------------

    /// <summary>Hands <paramref name="operation"/> to the queue with the aftermath of the file commands' own
    /// operations: the panels showing what it changed are read again, then a refusal, a failure or skipped items are
    /// told. For operations other commands build (packing, unpacking).</summary>
    /// <param name="alsoChanged">Folders changed before the operation (a destination folder made for it).</param>
    public void StartOperation(FileOperation operation, bool runImmediately, IReadOnlyList<VfsPath>? alsoChanged = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _ = Start(operation, runImmediately, alsoChanged: alsoChanged);
    }

    /// <summary>Runs a tool operation and waits for its affected panels to refresh.</summary>
    internal Task StartOperationAndWaitAsync(FileOperation operation) => Start(operation, runImmediately: true);

    /// <param name="alsoChanged">Folders changed before the operation (a destination folder made for it).</param>
    private Task Start(FileOperation operation, bool runImmediately, Action<OperationResult?>? afterRefresh = null,
        IReadOnlyList<VfsPath>? alsoChanged = null)
    {
        var completion = _queue.Enqueue(operation, runImmediately);
        _operations.Track(operation);
        return Tracked(FinishAsync(operation, completion, afterRefresh, alsoChanged ?? []));
    }

    /// <summary>After an operation, whatever its outcome: every tab showing a folder it changed is read again (marks
    /// of items that are gone go with them), then the user hears about a refusal, a failure or skipped items. Nothing
    /// is said after a cancel the user asked for, after an Abort the user chose in the error dialog, nor after a
    /// clean run.</summary>
    private async Task FinishAsync(FileOperation operation, Task<OperationResult> completion,
        Action<OperationResult?>? afterRefresh, IReadOnlyList<VfsPath> alsoChanged)
    {
        OperationResult? result = null;
        Exception? error = null;
        try
        {
            result = await completion;
        }
        catch (Exception e)
        {
            error = e;
        }

        try
        {
            var changed = operation.Sources.Select(s => s.Parent ?? s).Concat(alsoChanged).ToList();
            if (operation.Destination is { } destination) changed.Add(destination);
            await RefreshPanelsShowingAsync(changed, operation.Sources);
            afterRefresh?.Invoke(result);

            if (error is OperationRejectedException rejected)
                await _dialogs.ShowMessageAsync(OperationTexts.ForRejection(rejected));
            else if (error is not null && !(_interaction is DialogOperationInteraction asked && asked.WasAbortedByUser(error)))
                await _dialogs.ShowMessageAsync(Loc.Format("Operation_Failed", OperationTexts.ForFailure(error)),
                    error.Message);
            else if (result is { State: not OperationState.Cancelled, Skipped.Count: > 0 })
                await _dialogs.ShowSummaryAsync(result.Skipped);
        }
        catch (Exception e)
        {
            _reportFailure(e);
        }
    }

    /// <summary>Refreshes every tab, on both sides and in front or not, whose folder is one of
    /// <paramref name="folders"/> or lies inside one of <paramref name="removed"/> (a refresh of a folder that is gone
    /// moves to its nearest ancestor), and every tab listing entries of other folders, which may be among the changed
    /// ones.</summary>
    private async Task RefreshPanelsShowingAsync(IReadOnlyList<VfsPath> folders, IReadOnlyList<VfsPath> removed)
    {
        if (!_workspace.IsLoaded) return;
        var panels = _workspace.Left.Tabs.Concat(_workspace.Right.Tabs).Select(t => t.Panel).Distinct();
        var refreshes = panels
            .Where(p => p.CurrentPath != default &&
                        (folders.Contains(p.CurrentPath) || removed.Any(r => p.CurrentPath.IsSameOrDescendantOf(r)) ||
                         p.HasForeignEntries))
            .Select(p => p.RefreshAsync());
        await Task.WhenAll(refreshes);
    }

    private static void FocusRow(PanelViewModel panel, string name)
    {
        for (var i = 0; i < panel.Rows.Count; i++)
        {
            if (string.Equals(panel.Rows[i].Name, name, StringComparison.Ordinal))
            {
                panel.CursorIndex = i;
                return;
            }
        }
    }
}
