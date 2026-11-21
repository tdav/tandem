using Tandem.Core.Operations;
using Tandem.Core.Vfs;

namespace Tandem.Core.Tools.Rename;

public sealed record RenameStep(VfsPath From, VfsPath To);

public sealed record RenamedItem(VfsPath From, VfsPath To, EntryKind Kind, long Size, DateTimeOffset Modified);

public sealed class BatchRenameOperation : FileOperation
{
    private readonly ProviderRegistry _providers;
    private readonly RenameStep[] _steps;
    private readonly Dictionary<VfsPath, RenamedItem>? _undo;
    private readonly ItemErrors _errors;
    private readonly List<RenamedItem> _completed = [];
    private readonly List<SkippedItem> _needsRecovery = [];

    public BatchRenameOperation(ProviderRegistry providers, IReadOnlyList<RenameStep> steps,
        IOperationInteraction interaction, IReadOnlyList<RenamedItem>? undoOf = null)
    {
        _providers = providers;
        _steps = steps.ToArray();
        Sources = _steps.Select(step => step.From).ToArray();
        var parents = _steps.Select(step => step.From.Parent).Distinct().ToArray();
        Destination = parents.Length == 1 ? parents[0] : null;
        _errors = new ItemErrors(this, interaction);
        _undo = undoOf?.ToDictionary(item => item.To);
    }

    public IReadOnlyList<RenamedItem> Completed => _completed.AsReadOnly();
    public IReadOnlyList<SkippedItem> NeedsRecovery => _needsRecovery.AsReadOnly();
    public override OperationKind Kind => OperationKind.Rename;
    public override int ItemCount => _steps.Length;
    public override IReadOnlyList<VfsPath> Sources { get; }
    public override VfsPath? Destination { get; }

    public override async Task<IReadOnlyCollection<string>> GetVolumeKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in _steps.Select(step => step.From.Parent ?? step.From).Distinct())
            keys.Add($"{directory.ProviderId}:{await _providers.For(directory).GetVolumeKeyAsync(directory, ct).ConfigureAwait(false)}");
        return keys;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        foreach (var step in _steps)
            if (step.From.Parent is null || step.From.Parent != step.To.Parent ||
                !RenamePlanner.IsValidName(step.To.Name))
                throw new OperationRejectedException(RejectionReason.InvalidName, "Invalid rename target.",
                    step.From, step.To, step.To.Name);

        SetTotals(_steps.Length, 0);
        SetState(OperationState.Running);
        await RunInBatchAsync(_providers, Sources, async _ =>
        {
            foreach (var group in _steps.GroupBy(step => step.From.Parent!.Value))
                await RenameFolderAsync(group.Key, group.ToArray(), ct).ConfigureAwait(false);
        }, afterCommit: null, ct).ConfigureAwait(false);
    }

    private async Task RenameFolderAsync(VfsPath directory, RenameStep[] steps, CancellationToken ct)
    {
        var provider = _providers.For(directory);
        var sourceNames = steps.ToLookup(step => NameKey.Of(step.From.Name), StringComparer.OrdinalIgnoreCase);
        var stage = new HashSet<RenameStep>();
        foreach (var step in steps)
        {
            foreach (var owner in sourceNames[NameKey.Of(step.To.Name)])
            {
                if (step.From == step.To) continue;
                stage.Add(step);
                stage.Add(owner);
            }
        }

        var pending = new Dictionary<RenameStep, (VfsPath Part, FileEntry Snapshot)>();
        var finalized = new Dictionary<string, (VfsPath Part, RenamedItem Record)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var step in steps.Where(stage.Contains))
            {
                await WaitWhilePausedAsync(ct).ConfigureAwait(false);
                BeginItem(step.From.Name);
                if (await SnapshotAsync(provider, step, ct).ConfigureAwait(false) is not { } entry)
                {
                    CompleteFile();
                    continue;
                }
                var part = directory.Combine("." + Guid.NewGuid().ToString("N") + SafeReplace.RenameSuffix);
                if (await MoveAsync(provider, step.From, part, step.From, ct).ConfigureAwait(false))
                    pending.Add(step, (part, entry));
                else
                    CompleteFile();
            }

            foreach (var step in steps)
            {
                await WaitWhilePausedAsync(ct).ConfigureAwait(false);
                BeginItem(step.From.Name);
                if (pending.TryGetValue(step, out var item))
                {
                    if (await MoveAsync(provider, item.Part, step.To, step.From, ct).ConfigureAwait(false))
                    {
                        pending.Remove(step);
                        var record = await RecordCompletedAsync(provider, step, item.Snapshot).ConfigureAwait(false);
                        finalized[NameKey.Of(step.To.Name)] = (item.Part, record);
                    }
                }
                else if (!stage.Contains(step) && step.From != step.To &&
                         await SnapshotAsync(provider, step, ct).ConfigureAwait(false) is { } entry &&
                         await MoveAsync(provider, step.From, step.To, step.From, ct).ConfigureAwait(false))
                {
                    await RecordCompletedAsync(provider, step, entry).ConfigureAwait(false);
                }
                else if (stage.Contains(step))
                    continue;
                CompleteFile();
            }
        }
        finally
        {
            await RestoreStagedAsync(provider, pending.Select(pair => (pair.Key.From, pair.Value.Part)), finalized)
                .ConfigureAwait(false);
        }
    }

    private async Task<FileEntry?> SnapshotAsync(IFileSystemProvider provider, RenameStep step, CancellationToken ct)
    {
        FileEntry? entry = null;
        var read = await _errors.RunAsync(step.From, async () =>
        {
            entry = await provider.GetEntryAsync(step.From, ct).ConfigureAwait(false);
            if (entry is null && _undo is null) throw new VfsNotFoundException($"'{step.From.Path}' does not exist.");
        }, ct).ConfigureAwait(false);
        if (!read) return null;
        if (_undo is null || (_undo.TryGetValue(step.From, out var prior)
            && prior.From == step.To && Matches(entry, prior)))
            return entry;

        AddSkipped(new SkippedItem(step.From, SkipReason.ChangedSinceCompared));
        return null;
    }

    private async Task<bool> MoveAsync(IFileSystemProvider provider, VfsPath from, VfsPath to, VfsPath item,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await provider.MoveAsync(from, to, CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (VfsAlreadyExistsException)
            {
                AddSkipped(new SkippedItem(item, SkipReason.NameTaken));
                return false;
            }
            catch (Exception e) when (ItemErrors.IsItemError(e))
            {
                if (!await _errors.ShouldRetryAsync(item, e, ct).ConfigureAwait(false)) return false;
            }
        }
    }

    private async Task<RenamedItem> RecordCompletedAsync(IFileSystemProvider provider, RenameStep step, FileEntry snapshot)
    {
        try
        {
            snapshot = await provider.GetEntryAsync(step.To, CancellationToken.None).ConfigureAwait(false) ?? snapshot;
        }
        catch (Exception e) when (ItemErrors.IsItemError(e))
        {
            // The move succeeded. Keep the previous snapshot; undo still compares it to the actual destination.
        }
        var record = new RenamedItem(step.From, step.To, snapshot.Kind, snapshot.Size, snapshot.Modified);
        _completed.Add(record);
        return record;
    }

    private async Task RestoreStagedAsync(IFileSystemProvider provider, IEnumerable<(VfsPath Original, VfsPath Part)> items,
        Dictionary<string, (VfsPath Part, RenamedItem Record)> finalized)
    {
        var remaining = new Queue<(VfsPath Original, VfsPath Part)>(items);
        while (remaining.TryDequeue(out var item))
        {
            try
            {
                try
                {
                    await provider.MoveAsync(item.Part, item.Original, CancellationToken.None).ConfigureAwait(false);
                }
                catch (VfsAlreadyExistsException) when (finalized.TryGetValue(NameKey.Of(item.Original.Name), out var moved))
                {
                    // A finalized member of the same chain now holds this original name. Return it to staging so
                    // both files can be put back, provided it still matches the snapshot taken after its move.
                    var current = await provider.GetEntryAsync(moved.Record.To, CancellationToken.None).ConfigureAwait(false);
                    if (!Matches(current, moved.Record)) throw;
                    await provider.MoveAsync(moved.Record.To, moved.Part, CancellationToken.None).ConfigureAwait(false);
                    _completed.Remove(moved.Record);
                    finalized.Remove(NameKey.Of(moved.Record.To.Name));
                    remaining.Enqueue((moved.Record.From, moved.Part));
                    await provider.MoveAsync(item.Part, item.Original, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (ItemErrors.IsItemError(e))
            {
                var skipped = new SkippedItem(item.Part, SkipReason.Error, e.Message);
                _needsRecovery.Add(skipped);
                AddSkipped(skipped);
            }
        }
    }

    private static bool Matches(FileEntry? entry, RenamedItem record) => entry is not null &&
        record.Kind == entry.Kind && record.Size == entry.Size && record.Modified == entry.Modified;
}
