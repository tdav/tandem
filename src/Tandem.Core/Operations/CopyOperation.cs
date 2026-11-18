using Tandem.Core.Platform;
using Tandem.Core.Vfs;

namespace Tandem.Core.Operations;

/// <param name="VerifyContent">Re-read and compare each copied file (accepted, not acted on yet).</param>
/// <param name="NewName">The destination name; only with a single source.</param>
public sealed record CopyOptions(bool VerifyContent = false, string? NewName = null);

/// <summary>
/// Copies files, links and directory trees into <paramref name="destinationDirectory"/>, possibly across providers.
/// Scans first (<see cref="OperationState.Scanning"/>) to know the totals, then copies item by item.
/// For now an existing destination file or any I/O error fails the whole operation.
/// </summary>
public sealed class CopyOperation(ProviderRegistry providers, IReadOnlyList<VfsPath> sources,
    VfsPath destinationDirectory, CopyOptions options, IOperationInteraction interaction,
    INativeCopy? nativeCopy = null) : FileOperation
{
    private readonly ProviderRegistry _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    private readonly IReadOnlyList<VfsPath> _sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
    private readonly CopyOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly VfsPath _destinationDirectory = destinationDirectory;
    private readonly INativeCopy? _nativeCopy = nativeCopy;

    // Not consulted yet: conflicts and item errors currently fail the operation.
    private readonly IOperationInteraction _interaction =
        interaction ?? throw new ArgumentNullException(nameof(interaction));

    public override string Title => _sources.Count == 1
        ? $"Copy {_options.NewName ?? _sources[0].Name} to {_destinationDirectory.Path}"
        : $"Copy {_sources.Count} items to {_destinationDirectory.Path}";

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
        var targets = CopyEngine.ResolveTargets(_sources, _destinationDirectory, _options.NewName);
        var engine = new CopyEngine(_providers, this, _nativeCopy);

        SetState(OperationState.Scanning);
        var items = await engine.ScanAsync(targets, _destinationDirectory, ct).ConfigureAwait(false);

        SetState(OperationState.Running);
        var createdDirectories = new List<CopyItem>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (await engine.CopyItemAsync(item).ConfigureAwait(false) == CopyOutcome.DirectoryCreated)
                createdDirectories.Add(item);
        }

        // Children before parents, so writing a child does not bump its parent's time again.
        for (var i = createdDirectories.Count - 1; i >= 0; i--)
            await engine.ApplyDirectoryMetadataAsync(createdDirectories[i]).ConfigureAwait(false);
    }
}
