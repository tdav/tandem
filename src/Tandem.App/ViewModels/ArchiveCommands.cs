using System.ComponentModel;
using Tandem.App.Commands;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Archives;
using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels;

/// <summary>
/// Packing (Alt+F5) and unpacking (Alt+F9): the one place of the app that knows about archives as such. Browsing an
/// archive, copying out of it and changing a zip need nothing here: the archive is a provider the panels and the file
/// commands use like any other (<see cref="ArchiveMounts"/> opens it).
/// </summary>
/// <remarks>
/// Created and used on the UI thread; mounting and listing archives run off it. Packing applies to the active panel's
/// targets and proposes the passive panel's folder; unpacking applies to the archives among the marked targets (by
/// name; unavailable when there is none) or to the file under the cursor, whose content decides when it runs. Both need a passive panel showing a folder that can be written, and both run
/// their operation through <see cref="FileCommands.StartOperation"/>, so the panels are read again and errors told as
/// for copying. <see cref="CommandRegistry.RaiseCanExecuteChanged"/> is raised only when availability changes.
/// </remarks>
public sealed class ArchiveCommands : IDisposable
{
    private static readonly string[] Ids = [CommandIds.ArchivePack, CommandIds.ArchiveUnpack];

    private readonly WorkspaceViewModel _workspace;
    private readonly AppServices _services;
    private readonly ArchiveMounts _mounts;
    private readonly FileCommands _files;
    private readonly IOperationInteraction _interaction;
    private readonly CommandRegistry _commands;
    private readonly Action<Exception> _reportFailure;
    private readonly List<Task> _pending = [];
    private readonly List<PanelViewModel> _watched = [];
    private bool[] _available = [];
    private (PanelViewModel Panel, int Count)? _markedArchives;
    private bool _disposed;

    public ArchiveCommands(WorkspaceViewModel workspace, AppServices services, ArchiveMounts mounts, FileCommands files,
        IOperationInteraction interaction, CommandRegistry commands, Action<Exception> reportFailure)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _mounts = mounts ?? throw new ArgumentNullException(nameof(mounts));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));

        commands.Register(CommandIds.ArchivePack, () => Tracked(PackAsync()), CanPack);
        commands.Register(CommandIds.ArchiveUnpack, () => Tracked(UnpackAsync()), CanUnpack);
        workspace.PropertyChanged += OnWorkspacePropertyChanged;
        WatchPanels();
    }

    /// <summary>Completes when no pack or unpack command is running (its dialog closed and its operations handed to
    /// the queue). Exceptions are not rethrown.</summary>
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _workspace.PropertyChanged -= OnWorkspacePropertyChanged;
        foreach (var panel in _watched) panel.PropertyChanged -= OnPanelPropertyChanged;
        _watched.Clear();
    }

    private Task Tracked(Task task)
    {
        _pending.RemoveAll(t => t.IsCompleted);
        _pending.Add(task);
        return task;
    }

    // ---- Availability ------------------------------------------------------------------------------------

    private PanelViewModel? Active => _workspace.IsLoaded ? _workspace.ActivePanel : null;

    private PanelViewModel? Passive => _workspace.IsLoaded ? _workspace.PassivePanel : null;

    private bool HasTargets() =>
        Active is { } panel && panel.CurrentPath != default &&
        (panel.SelectedCount > 0 || panel.CursorRow is { IsParentLink: false });

    private bool PassiveCanTakeFiles() => Passive is { Error: null } passive && _files.CanWriteTo(passive.CurrentPath);

    private bool CanPack() => HasTargets() && PassiveCanTakeFiles();

    /// <summary>Marked rows count by name; a single file under the cursor (nothing marked) may be an archive by its
    /// content, which is looked at when the command runs.</summary>
    private bool CanUnpack()
    {
        if (!HasTargets() || !PassiveCanTakeFiles()) return false;
        var panel = Active!;
        return panel.SelectedCount == 0
            ? panel.CursorRow is { IsParentLink: false, Entry.Kind: EntryKind.File }
            : MarkedArchiveCount(panel) > 0;
    }

    /// <summary>How many marked rows are archives by name; kept until the marks, the folder or its rows change, so a
    /// cursor move over a large marked folder does not walk it again.</summary>
    private int MarkedArchiveCount(PanelViewModel panel)
    {
        if (_markedArchives is { } known && ReferenceEquals(known.Panel, panel)) return known.Count;
        var count = panel.GetOperationTargets().Count(IsArchive);
        _markedArchives = (panel, count);
        return count;
    }

    /// <summary>The archives among the panel's targets, by name. With nothing marked, the file under the cursor
    /// whatever its name (its content decides).</summary>
    private List<FileEntry> ArchivesAmong(PanelViewModel panel)
    {
        if (panel.SelectedCount == 0)
            return panel.CursorRow is { IsParentLink: false, Entry: { Kind: EntryKind.File } entry } ? [entry] : [];
        return panel.GetOperationTargets().Where(IsArchive).ToList();
    }

    private bool IsArchive(FileEntry entry) => entry.Kind == EntryKind.File && _mounts.IsArchiveName(entry.Name);

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.ActivePanel) or nameof(WorkspaceViewModel.PassivePanel))
            WatchPanels();
    }

    private void WatchPanels()
    {
        foreach (var panel in _watched) panel.PropertyChanged -= OnPanelPropertyChanged;
        _watched.Clear();
        _markedArchives = null;
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
        {
            if (e.PropertyName is not nameof(PanelViewModel.CursorIndex)) _markedArchives = null;
            UpdateAvailability();
        }
    }

    private void UpdateAvailability()
    {
        var available = Ids.Select(_commands.CanExecute).ToArray();
        if (available.SequenceEqual(_available)) return;
        _available = available;
        _commands.RaiseCanExecuteChanged();
    }

    // ---- Destinations ------------------------------------------------------------------------------------

    private ProviderRegistry Providers => _services.Providers;

    private bool CanWrite(VfsPath directory) =>
        Providers.TryGet(directory.ProviderId, out var provider) &&
        provider.Capabilities.HasFlag(ProviderCapabilities.Write);

    private Func<VfsPath, CancellationToken, Task<DestinationKind>> Probe()
    {
        var probe = DestinationParser.ProbeOf(Providers);
        return (path, ct) => Task.Run(() => probe(path, ct), ct);
    }

    /// <summary>The archive file a typed text names, as copying reads a destination (a relative text starts at
    /// <paramref name="baseDirectory"/>); or why it cannot be one: it is a folder, it exists, or its folder cannot be
    /// written.</summary>
    private async Task<(VfsPath? File, string? Error)> ArchiveFileOf(string text, VfsPath baseDirectory,
        VfsPath proposedDirectory)
    {
        var probe = Probe();
        var parsed = await DestinationParser.ParseAsync(text, baseDirectory, proposedDirectory, 1, null,
            _workspace.HomeDirectory, probe);
        if (parsed.Target is not { } target) return (null, parsed.Error);
        if (target.NewName is not { } name) return (null, Loc.Format("Archive_NeedsFileName", target.Directory.Path));
        var file = target.Directory.Combine(name);
        if (await probe(file, CancellationToken.None) != DestinationKind.Missing)
            return (null, Loc.Format("OperationError_AlreadyExists", file.Path));
        if (!CanWrite(target.Directory))
            return (null, Loc.Format("Archive_DestinationReadOnly", target.Directory.Path));
        return (file, null);
    }

    /// <summary>The folder a typed text names for unpacking, and whether it is still to be made (a missing last
    /// segment is a new folder); or why it cannot be one.</summary>
    private async Task<(VfsPath? Folder, bool Create, string? Error)> UnpackFolderOf(string text, VfsPath baseDirectory,
        VfsPath proposedDirectory)
    {
        var probe = Probe();
        var parsed = await DestinationParser.ParseAsync(text, baseDirectory, proposedDirectory, 1, null,
            _workspace.HomeDirectory, probe);
        if (parsed.Target is not { } target) return (null, false, parsed.Error);
        var folder = target.Directory;
        var create = target.CreateDirectory;
        if (target.NewName is { } name)
        {
            folder = folder.Combine(name);
            if (await probe(folder, CancellationToken.None) != DestinationKind.Missing)
                return (null, false, Loc.Format("Destination_NotAFolder", folder.Path));
            create = true;
        }
        if (!CanWrite(folder)) return (null, false, Loc.Format("Archive_DestinationReadOnly", folder.Path));
        return (folder, create, null);
    }

    // ---- Packing -----------------------------------------------------------------------------------------

    /// <summary>Alt+F5: the targets into a new archive, proposed in the passive panel's folder and named after the
    /// single target (without its extension) or the active panel's folder.</summary>
    private async Task PackAsync()
    {
        if (!CanPack() || Active is not { } active || Passive is not { } passive) return;
        var targets = active.GetOperationTargets();
        if (targets.Count == 0) return;
        var sources = targets.Select(t => t.Path).ToArray();
        var single = targets.Count == 1 ? targets[0].Name : null;
        var baseName = single is not null
            ? (targets[0].Kind == EntryKind.Directory ? single
                : _mounts.IsArchiveName(single) ? ArchiveFormats.NameWithoutSuffix(single)
                : Path.GetFileNameWithoutExtension(single))
            : active.CurrentPath.Name;
        if (string.IsNullOrEmpty(baseName)) baseName = Loc.Get("Archive_DefaultName");
        var formats = ArchiveFormats.Creatable;
        var to = passive.CurrentPath;
        var proposal = to.Combine(baseName + "." + ArchiveFormats.DefaultExtension(formats[0])).Path;
        var from = active.CurrentPath;

        var answer = await _services.Dialogs.AskPackAsync(new PackRequest(targets.Count, single, proposal, formats,
            async text => (await ArchiveFileOf(text, from, to)).Error));
        if (answer is null) return;

        // The dialog closes only on text its validator accepted; things may have changed since, so look again.
        var (file, error) = await ArchiveFileOf(answer.Destination, from, to);
        if (file is not { } archiveFile)
        {
            await _services.Dialogs.ShowMessageAsync(error!);
            return;
        }
        _files.StartOperation(new PackOperation(Providers, sources, archiveFile, answer.Format, _interaction,
            _services.Temp), answer.RunImmediately);
    }

    // ---- Unpacking ---------------------------------------------------------------------------------------

    /// <summary>Alt+F9: every archive among the targets into the passive panel's folder (or, with "each into its own
    /// folder", into a folder named after it there). Each archive is mounted and the entries of its root copied
    /// (<see cref="OperationKind.Unpack"/>), one operation per archive. An archive whose password the user declined
    /// is left out; one that cannot be opened is told.</summary>
    private async Task UnpackAsync()
    {
        if (!CanUnpack() || Active is not { } active || Passive is not { } passive) return;
        var archives = ArchivesAmong(active);
        if (archives.Count == 0) return;
        var to = passive.CurrentPath;
        var from = active.CurrentPath;
        if (active.SelectedCount == 0 && !IsArchive(archives[0]) && !await IsArchiveByContentAsync(archives[0])) return;

        var answer = await _services.Dialogs.AskUnpackAsync(new UnpackRequest(archives.Count,
            archives.Count == 1 ? archives[0].Name : null, to.Path, SeparateFolders: archives.Count > 1,
            async text => (await UnpackFolderOf(text, from, to)).Error));
        if (answer is null) return;

        var (folder, create, error) = await UnpackFolderOf(answer.Destination, from, to);
        if (folder is not { } destination)
        {
            await _services.Dialogs.ShowMessageAsync(error!);
            return;
        }
        if (create && !await CreateFolderAsync(destination)) return;

        // Folders of this run, so two archives of one name (a.zip, a.tar.gz) never share one.
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var archive in archives)
        {
            if (await RootEntriesAsync(archive.Path) is not { } entries || entries.Count == 0) continue;
            var target = destination;
            if (answer.SeparateFolders)
            {
                target = destination.Combine(UniqueName(folders, ArchiveFormats.NameWithoutSuffix(archive.Name)));
                if (!await CreateFolderAsync(target)) continue;
            }
            IReadOnlyList<VfsPath> alsoChanged = create || answer.SeparateFolders
                ? [destination.Parent ?? destination, destination]
                : [];
            _files.StartOperation(new CopyOperation(Providers, entries, target, new CopyOptions(), _interaction,
                _services.NativeCopy, kind: OperationKind.Unpack), answer.RunImmediately, alsoChanged);
        }
    }

    /// <summary><paramref name="name"/>, or "name 2", "name 3"… when this run has used it already; added to
    /// <paramref name="used"/>.</summary>
    private static string UniqueName(HashSet<string> used, string name)
    {
        var unique = name;
        for (var n = 2; !used.Add(unique); n++) unique = Loc.Format("Archive_FolderNumbered", name, n);
        return unique;
    }

    /// <summary>Whether the file's content is an archive's, looked at (and mounted) off the UI thread. When it is not,
    /// or it cannot be read, the user is told; a declined password says nothing.</summary>
    private async Task<bool> IsArchiveByContentAsync(FileEntry file)
    {
        try
        {
            if (await Task.Run(() => _mounts.TryMountAsync(file, byContent: true, CancellationToken.None)) is not null)
                return true;
            await _services.Dialogs.ShowMessageAsync(Loc.Format("Archive_NotAnArchive", file.Name));
        }
        catch (OperationCanceledException)
        {
            // the user declined the password
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await _services.Dialogs.ShowMessageAsync(OperationTexts.ForError(file.Path, e), e.Message);
        }
        return false;
    }

    /// <summary>The entries of the archive's root, mounted and listed off the UI thread; null when the user declined
    /// the password (nothing said), the archive could not be opened (told) or something unexpected failed
    /// (reported).</summary>
    private async Task<IReadOnlyList<VfsPath>?> RootEntriesAsync(VfsPath archive)
    {
        try
        {
            return await Task.Run(async () =>
            {
                var root = await _mounts.MountAsync(archive, CancellationToken.None).ConfigureAwait(false);
                var entries = new List<VfsPath>();
                await foreach (var entry in Providers.For(root).EnumerateAsync(root, CancellationToken.None)
                                   .ConfigureAwait(false))
                    entries.Add(entry.Path);
                return entries;
            });
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await _services.Dialogs.ShowMessageAsync(OperationTexts.ForError(archive, e), e.Message);
            return null;
        }
        catch (Exception e)
        {
            _reportFailure(e); // unexpected: the other archives are still unpacked
            return null;
        }
    }

    /// <summary>Makes the folder (and its missing parents) off the UI thread unless it is there; a failure is told.
    /// True when it exists afterwards.</summary>
    private async Task<bool> CreateFolderAsync(VfsPath folder)
    {
        var provider = Providers.For(folder);
        try
        {
            await Task.Run(async () =>
            {
                if (await provider.GetEntryAsync(folder, CancellationToken.None) is { IsDirectoryLike: true }) return;
                await provider.CreateDirectoryAsync(folder, CancellationToken.None);
            });
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await _services.Dialogs.ShowMessageAsync(OperationTexts.ForError(folder, e), e.Message);
            return false;
        }
    }
}
