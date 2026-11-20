using Tandem.Core.Platform;
using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

/// <summary>
/// Moves files, links and directory trees into <paramref name="destinationDirectory"/> (with
/// <see cref="CopyOptions.NewName"/> and the source's own folder, it renames in place).
/// <para>A source on the same volume as the destination (same provider and volume key, the destination's links
/// resolved) is moved with a single rename; a name conflict or an error is asked about as for a copy. Every other
/// source — on another volume or provider, or a directory that meets an existing directory and is merged into it —
/// is copied by the copy engine, with all its conflict, error, pause, cancel and verification rules. Each copied
/// file or link is deleted from the source right after it is in place at the destination, which for a file means
/// written without error and the same size as the source (and, with <see cref="CopyOptions.VerifyContent"/>, the
/// same content). A source directory is removed only when nothing is left in it. Skipped and failed items stay
/// where they are.</para>
/// <para>Cancelling keeps what was already moved; the item in flight stays whole at the source, without a partial
/// file at the destination.</para>
/// <para>A destination in a batching provider (<see cref="IBatchProvider"/>) holds the copies only once its batch
/// is committed, at the end of the move: the sources of copied items are deleted after that commit, and kept when
/// it fails.</para>
/// </summary>
public sealed class MoveOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources,
    VfsPath destinationDirectory, CopyOptions options, IOperationInteraction interaction,
    INativeCopy? nativeCopy = null) : FileOperation
{
    private readonly ProviderRegistry _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly IReadOnlyList<VfsPath> _sources =
        Array.AsReadOnly(sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources)));
    private readonly CopyOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly VfsPath _destinationDirectory = destinationDirectory;
    private readonly INativeCopy? _nativeCopy = nativeCopy;
    private readonly IOperationInteraction _interaction =
        interaction ?? throw new ArgumentNullException(nameof(interaction));

    public override OperationKind Kind => OperationKind.Move;

    public override int ItemCount => _sources.Count;

    public override IReadOnlyList<VfsPath> Sources => _sources;

    public override VfsPath? Destination => _destinationDirectory;

    /// <summary>"{provider id}:{volume key}" for every source and the destination directory, without duplicates.</summary>
    public override async Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in _sources.Append(_destinationDirectory))
            keys.Add(await VolumeOfAsync(path, ct).ConfigureAwait(false));
        return keys;
    }

    /// <summary>Batching providers among the sources and the destination collect the whole move and commit it at
    /// the end, also after a cancel or an error (<see cref="FileOperation.RunInBatchAsync"/>). When the destination
    /// is in the batch, a copied item is not at its destination until the commit, so the sources are deleted (files,
    /// then the emptied directories) only after a successful commit — in a batch of their own — and not at all when
    /// the commit or the move fails. Renames are not affected.</summary>
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        var planner = new CopyPlanner(_providers, this, new ItemErrors(this, _interaction), _options.VerifyContent);
        var deferred = new DeferredDeletions();
        return RunInBatchAsync(_providers, _sources.Append(_destinationDirectory),
            batch => MoveAsync(planner, batch, deferred, ct),
            afterCommit: () => deferred.IsEmpty ? Task.CompletedTask : DeleteDeferredAsync(planner, deferred), ct);
    }

    private async Task MoveAsync(CopyPlanner planner, BatchScope batch, DeferredDeletions deferred,
        CancellationToken ct)
    {
        var engine = new CopyEngine(planner, _providers, this, _interaction, _nativeCopy, checkSize: true);
        var destination = await planner.ResolveDestinationDirectoryAsync(_destinationDirectory, ct).ConfigureAwait(false);
        var targets = CopyPlanner.ResolveTargets(_sources, destination, _options.NewName);
        var defer = batch.Covers(destination);

        var (renames, copies) = await SplitAsync(planner, targets, destination, ct).ConfigureAwait(false);

        SetState(OperationState.Scanning);
        var items = await planner.ScanAsync(copies, ct, extraFiles: renames.Count).ConfigureAwait(false);
        await batch.PrepareReadAsync(CopyPlanner.FilesToRead(items), ct).ConfigureAwait(false);

        SetState(OperationState.Running);
        foreach (var (source, target) in renames)
            await engine.RenameAsync(source, target, ct).ConfigureAwait(false);

        var directories = new List<CopyItem>(); // created or merged into, in plan order
        var outcomes = await engine.CopyAllAsync(items, ct, (item, outcome, writtenTo) => outcome switch
        {
            CopyOutcome.Copied when defer => Remember(deferred.Files, (item.Source.Path, writtenTo)),
            CopyOutcome.Copied => DeleteSourceAsync(planner, item.Source.Path, writtenTo, ct),
            CopyOutcome.DirectoryCreated or CopyOutcome.DirectoryMerged => Remember(directories, item),
            _ => Task.CompletedTask,
        }).ConfigureAwait(false);

        // Children before parents, as in a copy; then the emptied source directories, deepest first. A directory
        // whose metadata could not be copied (and was skipped) stays at the source: removing it would lose its tags.
        var metadataSkipped = new HashSet<VfsPath>();
        for (var i = items.Count - 1; i >= 0; i--)
            if (outcomes[i] == CopyOutcome.DirectoryCreated &&
                !await engine.ApplyDirectoryMetadataAsync(items[i], ct).ConfigureAwait(false))
                metadataSkipped.Add(items[i].Source.Path);
        for (var i = directories.Count - 1; i >= 0; i--)
        {
            var directory = directories[i].Source.Path;
            if (metadataSkipped.Contains(directory)) continue;
            if (defer)
                deferred.Directories.Add(directory);
            else
                await RemoveIfEmptyAsync(planner, directory, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Sources whose copies wait for the destination's batch to commit: files and links in plan order (with
    /// the object each was written to), then directories deepest first.</summary>
    private sealed class DeferredDeletions
    {
        public List<(VfsPath Source, VfsPath WrittenTo)> Files { get; } = [];

        public List<VfsPath> Directories { get; } = [];

        public bool IsEmpty => Files.Count == 0 && Directories.Count == 0;
    }

    /// <summary>Deletes the sources of the committed copies, in a batch of the sources' own. It runs after the move
    /// itself has ended (perhaps cancelled), so nothing here is interrupted by a cancel; errors are asked about as
    /// for any deletion of a moved source.</summary>
    private Task DeleteDeferredAsync(CopyPlanner planner, DeferredDeletions deferred) =>
        RunInBatchAsync(_providers, _sources, async _ =>
        {
            foreach (var (source, writtenTo) in deferred.Files)
                await DeleteSourceAsync(planner, source, writtenTo, CancellationToken.None).ConfigureAwait(false);
            foreach (var directory in deferred.Directories)
                await RemoveIfEmptyAsync(planner, directory, CancellationToken.None).ConfigureAwait(false);
        }, afterCommit: null, CancellationToken.None);

    /// <summary>Splits the targets into single renames (same volume as the destination) and copies, before
    /// anything is touched. A directory whose destination is an existing directory is copied even on the same
    /// volume: merging cannot be one rename. A destination that already is the source itself (another spelling of
    /// it on a case-insensitive volume, or a path through a linked folder) is a pure rename when the name changes,
    /// and is refused when it does not: copying or merging it onto itself would destroy it.</summary>
    /// <exception cref="OperationRejectedException">An item would be moved onto itself.</exception>
    private async Task<(List<(VfsPath, VfsPath)> Renames, List<(VfsPath, VfsPath)> Copies)> SplitAsync(
        CopyPlanner planner, IReadOnlyList<(VfsPath Source, VfsPath Destination)> targets, VfsPath destinationDirectory,
        CancellationToken ct)
    {
        var destinationVolume = await VolumeOfAsync(destinationDirectory, ct).ConfigureAwait(false);
        var renames = new List<(VfsPath, VfsPath)>();
        var copies = new List<(VfsPath, VfsPath)>();
        foreach (var target in targets)
        {
            if (await planner.IsSameEntryAsync(target.Source, target.Destination, ct).ConfigureAwait(false))
            {
                if (string.Equals(target.Source.Name, target.Destination.Name, StringComparison.Ordinal))
                    throw CopyPlanner.OntoItself(target.Source, target.Destination);
                renames.Add(target);
            }
            else if (await VolumeOfAsync(target.Source, ct).ConfigureAwait(false) != destinationVolume ||
                await IsMergeAsync(target.Source, target.Destination, ct).ConfigureAwait(false))
                copies.Add(target);
            else
                renames.Add(target);
        }
        return (renames, copies);
    }

    private async Task<bool> IsMergeAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        var provider = _providers.For(source);
        var sourceEntry = await provider.GetEntryAsync(source, ct).ConfigureAwait(false);
        if (sourceEntry is not { Kind: EntryKind.Directory }) return false;
        var existing = await provider.GetEntryAsync(destination, ct).ConfigureAwait(false);
        return existing is { Kind: EntryKind.Directory };
    }

    private async Task<string> VolumeOfAsync(VfsPath path, CancellationToken ct) =>
        $"{path.ProviderId}:{await _providers.For(path).GetVolumeKeyAsync(path, ct).ConfigureAwait(false)}";

    private static Task Remember<T>(List<T> list, T item)
    {
        list.Add(item);
        return Task.CompletedTask;
    }

    /// <summary>Deletes a copied file or link (the link itself) from the source. The copy is in place by now, so
    /// this is not interrupted by a cancel; a failure is asked about, and skipping it leaves the item in both
    /// places. A source that is the very object just written (<paramref name="writtenTo"/>) is never deleted:
    /// that would delete the only copy.</summary>
    private async Task DeleteSourceAsync(CopyPlanner planner, VfsPath source, VfsPath writtenTo, CancellationToken ct)
    {
        if (await planner.IsSameEntryAsync(source, writtenTo, CancellationToken.None).ConfigureAwait(false))
        {
            AddSkipped(new SkippedItem(source, SkipReason.SourceIsDestination));
            return;
        }
        await planner.Errors.RunAsync(source,
            () => _providers.For(source).DeleteAsync(source, recursive: false, CancellationToken.None), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Removes a source directory whose contents were all moved; one with anything left in it (a skipped
    /// or failed item, or something added meanwhile) stays.</summary>
    private async Task RemoveIfEmptyAsync(CopyPlanner planner, VfsPath directory, CancellationToken ct)
    {
        var provider = _providers.For(directory);
        await planner.Errors.RunAsync(directory, async () =>
        {
            await foreach (var _ in provider.EnumerateAsync(directory, CancellationToken.None).ConfigureAwait(false))
                return; // not empty
            await provider.DeleteAsync(directory, recursive: false, CancellationToken.None).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
