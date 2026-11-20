using Tandem.Core.Platform;
using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

/// <param name="VerifyContent">Read each copied file and its source back and compare them (SHA-256) before the copy
/// is put in place; a difference is an item error (<see cref="ContentMismatchException"/>). Doubles the byte
/// total.</param>
/// <param name="NewName">The destination name; only with a single source.</param>
public sealed record CopyOptions(bool VerifyContent = false, string? NewName = null);

/// <summary>
/// Copies files, links and directory trees into <paramref name="destinationDirectory"/>, possibly across providers.
/// Scans first (<see cref="OperationState.Scanning"/>) to know the totals, then copies item by item.
/// An existing destination file and per-item I/O errors are put to <paramref name="interaction"/>; an existing
/// destination directory is merged into without asking.
/// <para>Unpacking is this copy with <paramref name="kind"/> <see cref="OperationKind.Unpack"/>; any kind other than
/// that and <see cref="OperationKind.Copy"/> is refused with <see cref="ArgumentException"/>.</para>
/// </summary>
public sealed class CopyOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources,
    VfsPath destinationDirectory, CopyOptions options, IOperationInteraction interaction,
    INativeCopy? nativeCopy = null, OperationKind kind = OperationKind.Copy) : FileOperation
{
    private readonly ProviderRegistry _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly IReadOnlyList<VfsPath> _sources =
        Array.AsReadOnly(sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources)));
    private readonly CopyOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly VfsPath _destinationDirectory = destinationDirectory;
    private readonly INativeCopy? _nativeCopy = nativeCopy;
    private readonly IOperationInteraction _interaction =
        interaction ?? throw new ArgumentNullException(nameof(interaction));
    private readonly OperationKind _kind = kind is OperationKind.Copy or OperationKind.Unpack
        ? kind
        : throw new ArgumentException($"A copy is named {OperationKind.Copy} or {OperationKind.Unpack}, not {kind}.",
            nameof(kind));

    /// <summary><see cref="OperationKind.Copy"/>, or <see cref="OperationKind.Unpack"/> for a copy out of an archive:
    /// the same work under the name the interface shows.</summary>
    public override OperationKind Kind => _kind;

    public override int ItemCount => _sources.Count;

    public override IReadOnlyList<VfsPath> Sources => _sources;

    public override VfsPath? Destination => _destinationDirectory;

    /// <summary>"{provider id}:{volume key}" for every source and the destination directory, without duplicates.</summary>
    public override async Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in _sources.Append(_destinationDirectory))
            keys.Add($"{path.ProviderId}:{await _providers.For(path).GetVolumeKeyAsync(path, ct).ConfigureAwait(false)}");
        return keys;
    }

    /// <summary>Batching providers among the sources and the destination collect the whole copy and commit it at the
    /// end, also after a cancel or an error (<see cref="FileOperation.RunInBatchAsync"/>).</summary>
    protected override Task ExecuteAsync(CancellationToken ct) =>
        RunInBatchAsync(_providers, _sources.Append(_destinationDirectory), batch => CopyAsync(batch, ct),
            afterCommit: null, ct);

    private async Task CopyAsync(BatchScope batch, CancellationToken ct)
    {
        var planner = new CopyPlanner(_providers, this, new ItemErrors(this, _interaction), _options.VerifyContent);
        var engine = new CopyEngine(planner, _providers, this, _interaction, _nativeCopy);
        var destination = await planner.ResolveDestinationDirectoryAsync(_destinationDirectory, ct).ConfigureAwait(false);
        var targets = CopyPlanner.ResolveTargets(_sources, destination, _options.NewName);
        await planner.RejectTargetsOntoThemselvesAsync(targets, ct).ConfigureAwait(false);

        SetState(OperationState.Scanning);
        var items = await planner.ScanAsync(targets, ct).ConfigureAwait(false);
        await batch.PrepareReadAsync(CopyPlanner.FilesToRead(items), ct).ConfigureAwait(false);

        SetState(OperationState.Running);
        var outcomes = await engine.CopyAllAsync(items, ct).ConfigureAwait(false);

        // Children before parents, so writing a child does not bump its parent's time again.
        for (var i = items.Count - 1; i >= 0; i--)
            if (outcomes[i] == CopyOutcome.DirectoryCreated)
                await engine.ApplyDirectoryMetadataAsync(items[i], ct).ConfigureAwait(false);
    }
}
