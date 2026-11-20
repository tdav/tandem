using Tandem.Core.Platform;
using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

/// <summary>
/// Deletes the selected items, to the trash or for good. Symbolic links are never followed: deleting a link
/// removes the link only.
/// <para>To the trash (<paramref name="toTrash"/>): each selected item goes to <paramref name="trash"/> whole, in
/// one call, without walking into it. Only items on the local disk can go there; asking for the trash without a
/// trash service or for another provider is refused (<see cref="RejectionReason.TrashNotSupported"/>) before
/// anything is touched.</para>
/// <para>For good: the items are scanned first (<see cref="OperationState.Scanning"/>), then removed depth-first,
/// each directory after its contents, one entry at a time. An error is put to <paramref name="interaction"/>;
/// a skipped item stays, and so do the directories that hold it, without a question of their own.</para>
/// <para>Cancelling stops between items.</para>
/// </summary>
public sealed class DeleteOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources,
    bool toTrash, ITrash? trash, IOperationInteraction interaction) : FileOperation
{
    private readonly ProviderRegistry _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly IReadOnlyList<VfsPath> _sources =
        Array.AsReadOnly(sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources)));
    private readonly bool _toTrash = toTrash;
    private readonly ITrash? _trash = trash;
    private readonly IOperationInteraction _interaction =
        interaction ?? throw new ArgumentNullException(nameof(interaction));

    public override OperationKind Kind => OperationKind.Delete;

    public override int ItemCount => _sources.Count;

    public override IReadOnlyList<VfsPath> Sources => _sources;

    public override VfsPath? Destination => null;

    /// <summary>"{provider id}:{volume key}" for every source, without duplicates.</summary>
    public override async Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in _sources)
            keys.Add($"{path.ProviderId}:{await _providers.For(path).GetVolumeKeyAsync(path, ct).ConfigureAwait(false)}");
        return keys;
    }

    /// <summary>Batching providers among the sources collect the deletions and commit them at the end, also after a
    /// cancel or an error (<see cref="FileOperation.RunInBatchAsync"/>).</summary>
    protected override Task ExecuteAsync(CancellationToken ct)
    {
        var errors = new ItemErrors(this, _interaction);
        return RunInBatchAsync(_providers, _sources,
            _ => _toTrash ? MoveToTrashAsync(errors, ct) : DeletePermanentlyAsync(errors, ct), afterCommit: null, ct);
    }

    private async Task MoveToTrashAsync(ItemErrors errors, CancellationToken ct)
    {
        foreach (var source in _sources)
            if (_trash is null || !string.Equals(source.ProviderId, VfsPath.LocalProviderId, StringComparison.Ordinal))
                throw new OperationRejectedException(RejectionReason.TrashNotSupported,
                    $"'{source}' cannot be moved to the trash.", source);

        SetState(OperationState.Running);
        SetTotals(_sources.Count, 0);
        foreach (var source in _sources)
        {
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            BeginItem(source.Path);
            // One call per item; it is not interrupted, so an item is never left half in the trash.
            await errors.RunAsync(source, () => _trash!.MoveToTrashAsync(source.Path, CancellationToken.None), ct)
                .ConfigureAwait(false);
            CompleteFile();
        }
    }

    private async Task DeletePermanentlyAsync(ItemErrors errors, CancellationToken ct)
    {
        SetState(OperationState.Scanning);
        var (items, kept) = await ScanAsync(errors, ct).ConfigureAwait(false);

        SetState(OperationState.Running);
        foreach (var item in items)
        {
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            var path = item.Path;
            if (item.Kind == EntryKind.Directory && kept.Contains(path)) continue; // something inside it stays
            BeginItem(path.Path);
            var deleted = await errors.RunAsync(path,
                () => _providers.For(path).DeleteAsync(path, recursive: false, CancellationToken.None), ct)
                .ConfigureAwait(false);
            if (!deleted) KeepAncestors(kept, path);
            if (item.Kind != EntryKind.Directory) CompleteFile();
        }
    }

    /// <summary>Lists everything to delete, children before their directory (links are not followed), and sets the
    /// totals. A source that cannot be found, or a directory that cannot be listed, goes to the error question;
    /// skipping it keeps it (and its directories) out of the deletion.</summary>
    private async Task<(List<FileEntry> Items, HashSet<VfsPath> Kept)> ScanAsync(ItemErrors errors,
        CancellationToken ct)
    {
        var items = new List<FileEntry>();
        var kept = new HashSet<VfsPath>();
        long files = 0;
        foreach (var source in _sources)
        {
            FileEntry? entry = null;
            if (!await errors.RunAsync(source, async () =>
                {
                    entry = await _providers.For(source).GetEntryAsync(source, ct).ConfigureAwait(false)
                            ?? throw new VfsNotFoundException($"'{source.Path}' does not exist.");
                }, ct).ConfigureAwait(false))
                continue;
            await AddAsync(entry!).ConfigureAwait(false);
        }
        SetTotals(files, 0);
        return (items, kept);

        async Task AddAsync(FileEntry entry)
        {
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            if (entry.Kind == EntryKind.Directory)
            {
                var children = new List<FileEntry>();
                var listed = await errors.RunAsync(entry.Path, async () =>
                {
                    children.Clear();
                    await foreach (var child in _providers.For(entry.Path).EnumerateAsync(entry.Path, ct)
                                       .ConfigureAwait(false))
                        children.Add(child);
                }, ct).ConfigureAwait(false);
                if (!listed)
                {
                    KeepAncestors(kept, entry.Path);
                    return;
                }
                foreach (var child in children) await AddAsync(child).ConfigureAwait(false);
            }
            else
            {
                files++;
            }
            items.Add(entry);
        }
    }

    /// <summary>The directories above <paramref name="path"/> cannot be removed once it stays.</summary>
    private static void KeepAncestors(HashSet<VfsPath> kept, VfsPath path)
    {
        for (var parent = path.Parent; parent is { } p && kept.Add(p); parent = p.Parent)
        {
        }
    }
}
