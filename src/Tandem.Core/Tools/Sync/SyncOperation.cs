using Tandem.Core.Operations;
using Tandem.Core.Platform;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tools.Sync;

public sealed record SyncDecision(SyncItem Item, SyncAction Action);

public sealed class SyncOperation : FileOperation
{
    private readonly ProviderRegistry _providers;
    private readonly SyncComparison _comparison;
    private readonly SyncDecision[] _decisions;
    private readonly IOperationInteraction _interaction;
    private readonly ITrash? _trash;
    private readonly INativeCopy? _nativeCopy;

    public SyncOperation(ProviderRegistry providers, SyncComparison comparison, IReadOnlyList<SyncDecision> decisions,
        IOperationInteraction interaction, ITrash? trash = null, INativeCopy? nativeCopy = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(decisions);
        ArgumentNullException.ThrowIfNull(interaction);
        _providers = providers;
        _comparison = comparison;
        _interaction = interaction;
        _trash = trash;
        _nativeCopy = nativeCopy;
        var items = comparison.Items.ToHashSet();
        var seen = new HashSet<SyncItem>();
        foreach (var decision in decisions)
        {
            if (!items.Contains(decision.Item) || !seen.Add(decision.Item) ||
                !decision.Item.AllowedActions.Contains(decision.Action))
                throw new ArgumentException("The decision is not allowed by the comparison.", nameof(decisions));
        }
        _decisions = decisions.Where(decision => decision.Action != SyncAction.None)
            .OrderBy(decision => decision.Action).ToArray();
    }

    public override OperationKind Kind => OperationKind.Sync;
    public override int ItemCount => _decisions.Length;
    public override IReadOnlyList<VfsPath> Sources => [_comparison.LeftRoot];
    public override VfsPath? Destination => _comparison.RightRoot;

    public override async Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Sources.Append(_comparison.RightRoot))
            keys.Add($"{path.ProviderId}:{await _providers.For(path).GetVolumeKeyAsync(path, ct).ConfigureAwait(false)}");
        return keys;
    }

    protected override Task ExecuteAsync(CancellationToken ct) =>
        RunInBatchAsync(_providers, Sources.Append(_comparison.RightRoot), batch => RunDecisionsAsync(batch, ct), null, ct);

    private async Task RunDecisionsAsync(BatchScope batch, CancellationToken ct)
    {
        var choices = new PlannedInteraction(_interaction);
        var errors = new ItemErrors(this, choices);
        var planner = new CopyPlanner(_providers, this, errors, verifyContent: false);
        var engine = new CopyEngine(planner, _providers, this, choices, _nativeCopy);
        SetTotals(_decisions.Length, _decisions.Sum(decision => BytesToCopy(decision)));
        await batch.PrepareReadAsync(_decisions.Select(CopySource).OfType<FileEntry>()
            .Where(entry => entry.Kind == EntryKind.File).Select(entry => entry.Path).ToArray(), ct).ConfigureAwait(false);

        SetState(OperationState.Running);
        foreach (var decision in _decisions)
        {
            await WaitWhilePausedAsync(ct).ConfigureAwait(false);
            var item = decision.Item;
            var leftPath = item.Left?.Path ?? item.LeftPath ?? At(_comparison.LeftRoot, item.RelativePath);
            var rightPath = item.Right?.Path ?? item.RightPath ?? At(_comparison.RightRoot, item.RelativePath);
            var fromLeft = decision.Action is SyncAction.CopyToRight or SyncAction.DeleteLeft;
            var source = fromLeft ? leftPath : rightPath;
            var destination = fromLeft ? rightPath : leftPath;
            var sourceRoot = fromLeft ? _comparison.LeftRoot : _comparison.RightRoot;
            var destinationRoot = fromLeft ? _comparison.RightRoot : _comparison.LeftRoot;
            var expectedSource = fromLeft ? item.Left! : item.Right!;
            var expectedDestination = fromLeft ? item.Right : item.Left;
            var isCopy = decision.Action is SyncAction.CopyToRight or SyncAction.CopyToLeft;
            BeginItem(source.Path);
            bool changed = false, countedByEngine = false;
            try
            {
                choices.Prepare(expectedDestination, async () =>
                {
                    var l = await GetAsync(leftPath, ct).ConfigureAwait(false);
                    var r = await GetAsync(rightPath, ct).ConfigureAwait(false);
                    if (!Matches(expectedSource, fromLeft ? l : r) ||
                        (isCopy && !Matches(expectedDestination, fromLeft ? r : l)) ||
                        !await SafeParentsAsync(sourceRoot, source, allowMissing: false, ct).ConfigureAwait(false) ||
                        (isCopy && !await SafeParentsAsync(destinationRoot, destination, allowMissing: true, ct)
                            .ConfigureAwait(false)))
                        throw new ChangedSinceComparedException();
                });
                var completed = await errors.RunAsync(source, async () =>
                {
                    await choices.ValidateAsync().ConfigureAwait(false);
                    var provider = _providers.For(source);
                    if (expectedSource.Kind == EntryKind.Directory &&
                        !await StillEmptyAsync(provider, source, ct).ConfigureAwait(false))
                        throw new ChangedSinceComparedException();

                    if (isCopy)
                    {
                        var target = _providers.For(destination);
                        await target.CreateDirectoryAsync(destination.Parent!.Value, ct).ConfigureAwait(false);
                    }
                    else if (_trash is not null && provider.Capabilities.HasFlag(ProviderCapabilities.Trash))
                    {
                        await _trash.MoveToTrashAsync(source.Path, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await provider.DeleteAsync(source, recursive: false, ct).ConfigureAwait(false);
                    }
                }, ct).ConfigureAwait(false);
                if (completed && isCopy) await CopyAsync().ConfigureAwait(false);
            }
            catch (ChangedSinceComparedException)
            {
                changed = true;
            }

            if (changed) AddSkipped(new(source, SkipReason.ChangedSinceCompared));
            if (!countedByEngine)
            {
                DropBytesFromTotal(BytesToCopy(decision));
                CompleteFile();
            }

            async Task CopyAsync()
            {
                var copy = new CopyItem(expectedSource, destination);
                var outcomes = await engine.CopyAllAsync([copy], ct).ConfigureAwait(false);
                countedByEngine = expectedSource.Kind != EntryKind.Directory;
                if (outcomes[0] == CopyOutcome.DirectoryCreated)
                    await engine.ApplyDirectoryMetadataAsync(copy, ct).ConfigureAwait(false);
            }
        }
    }

    private Task<FileEntry?> GetAsync(VfsPath path, CancellationToken ct) => _providers.For(path).GetEntryAsync(path, ct);

    private async Task<bool> SafeParentsAsync(VfsPath root, VfsPath path, bool allowMissing, CancellationToken ct)
    {
        if (!path.IsSameOrDescendantOf(root) || path == root || path.Parent is not { } parent)
            return false;
        var directories = new Stack<VfsPath>();
        for (VfsPath? current = parent; current is { } directory; current = directory.Parent)
        {
            directories.Push(directory);
            if (directory == root) break;
        }

        while (directories.TryPop(out var folder))
        {
            var entry = await GetAsync(folder, ct).ConfigureAwait(false);
            if (entry is null && allowMissing && folder != root) return true;
            if (entry?.Kind != EntryKind.Directory) return false;
        }
        return true;
    }

    private static async Task<bool> StillEmptyAsync(IFileSystemProvider provider, VfsPath path, CancellationToken ct)
    {
        await foreach (var _ in provider.EnumerateAsync(path, ct).ConfigureAwait(false)) return false;
        return true;
    }

    private static bool Matches(FileEntry? expected, FileEntry? actual) =>
        expected is null ? actual is null : actual is not null && expected.Kind == actual.Kind &&
            expected.Size == actual.Size && expected.Modified == actual.Modified &&
            string.Equals(expected.LinkTarget, actual.LinkTarget, StringComparison.Ordinal);

    private static FileEntry? CopySource(SyncDecision decision) => decision.Action switch
    {
        SyncAction.CopyToRight => decision.Item.Left,
        SyncAction.CopyToLeft => decision.Item.Right,
        _ => null,
    };

    private static long BytesToCopy(SyncDecision decision) => CopySource(decision) is { Kind: EntryKind.File } entry
        ? entry.Size : 0;

    private static VfsPath At(VfsPath root, string relative) => relative.Split('/').Aggregate(root, (p, s) => p.Combine(s));

    private sealed class ChangedSinceComparedException : Exception;

    private sealed class PlannedInteraction(IOperationInteraction actual) : IOperationInteraction
    {
        private FileEntry? _destination;
        private Func<Task> _validate = () => Task.CompletedTask;

        public void Prepare(FileEntry? destination, Func<Task> validate) =>
            (_destination, _validate) = (destination, validate);

        public Task ValidateAsync() => _validate();

        public async Task<ConflictDecision> ResolveConflictAsync(FileEntry source, FileEntry existing, CancellationToken ct)
        {
            if (_destination is null || existing.Path != _destination.Path || !Matches(_destination, existing))
                throw new ChangedSinceComparedException();
            await ValidateAsync().ConfigureAwait(false);
            return new(ConflictAction.Overwrite, ApplyToAll: false);
        }

        public async Task<ErrorAction> ResolveErrorAsync(VfsPath item, Exception error, CancellationToken ct)
        {
            var result = await actual.ResolveErrorAsync(item, error, ct).ConfigureAwait(false);
            if (result == ErrorAction.Retry) await ValidateAsync().ConfigureAwait(false);
            return result;
        }
    }
}
