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

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var engine = new CopyEngine(_providers, this, _interaction, _nativeCopy, _options.VerifyContent,
            checkSize: true);
        var destination = await engine.ResolveDestinationDirectoryAsync(_destinationDirectory, ct).ConfigureAwait(false);
        var targets = CopyEngine.ResolveTargets(_sources, destination, _options.NewName);

        SetState(OperationState.Scanning);
        var (renames, copies) = await SplitAsync(targets, destination, ct).ConfigureAwait(false);
        var items = await engine.ScanAsync(copies, ct, extraFiles: renames.Count).ConfigureAwait(false);

        SetState(OperationState.Running);
        foreach (var (source, target) in renames)
            await engine.RenameAsync(source, target, ct).ConfigureAwait(false);

        var directories = new List<CopyItem>(); // created or merged into, in plan order
        var outcomes = await engine.CopyAllAsync(items, ct, (item, outcome) => outcome switch
        {
            CopyOutcome.Copied => DeleteSourceAsync(engine, item.Source.Path, ct),
            CopyOutcome.DirectoryCreated or CopyOutcome.DirectoryMerged => Remember(directories, item),
            _ => Task.CompletedTask,
        }).ConfigureAwait(false);

        // Children before parents, as in a copy; then the emptied source directories, deepest first.
        for (var i = items.Count - 1; i >= 0; i--)
            if (outcomes[i] == CopyOutcome.DirectoryCreated)
                await engine.ApplyDirectoryMetadataAsync(items[i]).ConfigureAwait(false);
        for (var i = directories.Count - 1; i >= 0; i--)
            await RemoveIfEmptyAsync(engine, directories[i].Source.Path, ct).ConfigureAwait(false);
    }

    /// <summary>Splits the targets into single renames (same volume as the destination) and copies. A directory
    /// whose destination is an existing directory is copied even on the same volume: merging cannot be one
    /// rename.</summary>
    private async Task<(List<(VfsPath, VfsPath)> Renames, List<(VfsPath, VfsPath)> Copies)> SplitAsync(
        IReadOnlyList<(VfsPath Source, VfsPath Destination)> targets, VfsPath destinationDirectory, CancellationToken ct)
    {
        var destinationVolume = await VolumeOfAsync(destinationDirectory, ct).ConfigureAwait(false);
        var renames = new List<(VfsPath, VfsPath)>();
        var copies = new List<(VfsPath, VfsPath)>();
        foreach (var target in targets)
        {
            if (await VolumeOfAsync(target.Source, ct).ConfigureAwait(false) != destinationVolume ||
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

    private static Task Remember(List<CopyItem> directories, CopyItem item)
    {
        directories.Add(item);
        return Task.CompletedTask;
    }

    /// <summary>Deletes a copied file or link (the link itself) from the source. The copy is in place by now, so
    /// this is not interrupted by a cancel; a failure is asked about, and skipping it leaves the item in both
    /// places.</summary>
    private async Task DeleteSourceAsync(CopyEngine engine, VfsPath source, CancellationToken ct) =>
        await engine.Errors.RunAsync(source,
            () => _providers.For(source).DeleteAsync(source, recursive: false, CancellationToken.None), ct)
            .ConfigureAwait(false);

    /// <summary>Removes a source directory whose contents were all moved; one with anything left in it (a skipped
    /// or failed item, or something added meanwhile) stays.</summary>
    private async Task RemoveIfEmptyAsync(CopyEngine engine, VfsPath directory, CancellationToken ct)
    {
        var provider = _providers.For(directory);
        await engine.Errors.RunAsync(directory, async () =>
        {
            await foreach (var _ in provider.EnumerateAsync(directory, CancellationToken.None).ConfigureAwait(false))
                return; // not empty
            await provider.DeleteAsync(directory, recursive: false, CancellationToken.None).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }
}
