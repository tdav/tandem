using Tandem.Core.Platform;
using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

/// <param name="VerifyContent">Re-read and compare each copied file (accepted, not acted on yet).</param>
/// <param name="NewName">The destination name; only with a single source.</param>
public sealed record CopyOptions(bool VerifyContent = false, string? NewName = null);

/// <summary>
/// Copies files, links and directory trees into <paramref name="destinationDirectory"/>, possibly across providers.
/// Scans first (<see cref="OperationState.Scanning"/>) to know the totals, then copies item by item.
/// An existing destination file and per-item I/O errors are put to <paramref name="interaction"/>; an existing
/// destination directory is merged into without asking.
/// </summary>
public sealed class CopyOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources,
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

    public override OperationKind Kind => OperationKind.Copy;

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

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var engine = new CopyEngine(_providers, this, _interaction, _nativeCopy);
        var destination = await engine.ResolveDestinationDirectoryAsync(_destinationDirectory, ct).ConfigureAwait(false);
        var targets = CopyEngine.ResolveTargets(_sources, destination, _options.NewName);

        SetState(OperationState.Scanning);
        var items = await engine.ScanAsync(targets, ct).ConfigureAwait(false);

        SetState(OperationState.Running);
        var outcomes = await engine.CopyAllAsync(items, ct).ConfigureAwait(false);

        // Children before parents, so writing a child does not bump its parent's time again.
        for (var i = items.Count - 1; i >= 0; i--)
            if (outcomes[i] == CopyOutcome.DirectoryCreated)
                await engine.ApplyDirectoryMetadataAsync(items[i]).ConfigureAwait(false);
    }
}
