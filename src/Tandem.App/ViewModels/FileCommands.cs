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
/// of the active panel; nothing to apply to (the cursor on "..", an empty folder) disables them. Copy and move go to
/// the passive panel's source and are disabled while that panel shows no folder or an error.
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
    private readonly List<PanelViewModel> _watched = [];
    private bool[] _available = [];
    private InlineRenameViewModel? _rename;
    private bool _disposed;

    private static readonly string[] Ids =
    [
        CommandIds.FileCopy, CommandIds.FileMove, CommandIds.FileMkdir, CommandIds.FileDelete,
        CommandIds.FileDeletePermanent, CommandIds.FileRename, CommandIds.SelectByMask, CommandIds.UnselectByMask,
    ];

    public FileCommands(WorkspaceViewModel workspace, ProviderRegistry providers, OperationQueue queue,
        IDialogService dialogs, ITrash? trash, INativeCopy? nativeCopy, CommandRegistry commands,
        OperationsViewModel operations, IOperationInteraction interaction, Action<Exception> reportFailure)
    {
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

    private bool CanWrite(PanelViewModel panel) =>
        panel.CurrentPath != default && _providers.For(panel.CurrentPath).Capabilities.HasFlag(ProviderCapabilities.Write);

    private bool HasTargets() =>
        Active is { } panel && panel.CurrentPath != default &&
        (panel.SelectedCount > 0 || panel.CursorRow is { IsParentLink: false });

    private bool HasCursorEntry() =>
        Active is { } panel && CanWrite(panel) && panel.CursorRow is { IsParentLink: false };

    private bool HasRows() => Active is { TotalCount: > 0 };

    private bool CanMakeDirectory() => Active is { Error: null } panel && CanWrite(panel);

    /// <summary>Copying needs a writable destination only; moving and deleting change the source too.</summary>
    private bool CanCopyOrMove() =>
        HasTargets() && Passive is { Error: null } passive && CanWrite(passive);

    private bool CanMove() => CanCopyOrMove() && CanWrite(Active!);

    private bool CanDelete() => HasTargets() && CanWrite(Active!);

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

    private async Task CopyOrMoveAsync(bool move)
    {
        if (!(move ? CanMove() : CanCopyOrMove()) || Active is not { } active || Passive is not { } passive) return;
        var targets = active.GetOperationTargets();
        if (targets.Count == 0) return;

        var single = targets.Count == 1 ? targets[0].Name : null;
        var (from, to) = (active.CurrentPath, passive.CurrentPath);
        var proposal = single is null ? to.Path : to.Combine(single).Path;
        var probe = OffTheUiThread(DestinationParser.ProbeOf(_providers));
        Task<DestinationParseResult> Parse(string text) => DestinationParser.ParseAsync(text, from, to,
            targets.Count, single, _workspace.HomeDirectory, probe);

        var answer = await _dialogs.AskCopyMoveAsync(new CopyMoveRequest(move, targets.Count, single, proposal,
            async text => (await Parse(text)).Error));
        if (answer is null) return;

        // The dialog closes only on text its validator accepted; things may have changed since, so look again.
        var parsed = await Parse(answer.Destination);
        if (parsed.Target is not { } target)
        {
            await _dialogs.ShowMessageAsync(parsed.Error!);
            return;
        }
        if (target.CreateDirectory && !await CreateDirectoryAsync(target.Directory)) return;
        // A folder made for the copy shows up in its parent, which the passive panel may be showing.
        IReadOnlyList<VfsPath> alsoChanged = target.CreateDirectory
            ? [target.Directory.Parent ?? target.Directory, to]
            : [];

        var sources = targets.Select(t => t.Path).ToArray();
        var options = new CopyOptions(answer.VerifyContent, target.NewName);
        FileOperation operation = move
            ? new MoveOperation(_providers, sources, target.Directory, options, _interaction, _nativeCopy)
            : new CopyOperation(_providers, sources, target.Directory, options, _interaction, _nativeCopy);
        Start(operation, answer.RunImmediately, alsoChanged: alsoChanged);
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
        var trimmed = text.EndsWith('/') ? text[..^1] : text;
        var segments = trimmed.Split('/');
        if (text.StartsWith('/') || segments.Any(s => s.Length == 0 || s is "." or ".." || s.Contains('\0')))
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

        var provider = _providers.For(panel.CurrentPath);
        var toTrash = !permanent && _trash is not null && provider.Capabilities.HasFlag(ProviderCapabilities.Trash);
        var single = targets.Count == 1 ? targets[0].Name : null;
        if (!await _dialogs.ConfirmDeleteAsync(targets.Count, single, permanent: !toTrash)) return;

        Start(new DeleteOperation(_providers, targets.Select(t => t.Path).ToArray(), toTrash, _trash, _interaction),
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
        var nameTaken = panel.Rows.Any(r => !r.IsParentLink && string.Equals(r.Name, newName, StringComparison.Ordinal));
        var operation = new MoveOperation(_providers, [entry.Path], directory, new CopyOptions(NewName: newName),
            _interaction, _nativeCopy);
        Start(operation, runImmediately: true, afterRefresh: result =>
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

    /// <param name="alsoChanged">Folders changed before the operation (a destination folder made for it).</param>
    private void Start(FileOperation operation, bool runImmediately, Action<OperationResult?>? afterRefresh = null,
        IReadOnlyList<VfsPath>? alsoChanged = null)
    {
        var completion = _queue.Enqueue(operation, runImmediately);
        _operations.Track(operation);
        Tracked(FinishAsync(operation, completion, afterRefresh, alsoChanged ?? []));
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
    /// moves to its nearest ancestor).</summary>
    private async Task RefreshPanelsShowingAsync(IReadOnlyList<VfsPath> folders, IReadOnlyList<VfsPath> removed)
    {
        if (!_workspace.IsLoaded) return;
        var panels = _workspace.Left.Tabs.Concat(_workspace.Right.Tabs).Select(t => t.Panel).Distinct();
        var refreshes = panels
            .Where(p => p.CurrentPath != default &&
                        (folders.Contains(p.CurrentPath) || removed.Any(r => p.CurrentPath.IsSameOrDescendantOf(r))))
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
